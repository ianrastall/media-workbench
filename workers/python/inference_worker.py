"""Private Python 3.12 ML child. Runs the bundled recipe, and added SD 1.5 models with its components.

Changes to what a verified installation means must bump inference_setup.ADAPTER_VERSION.
"""
import argparse
from contextlib import redirect_stdout
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
import time

# Isolated mode (-I) omits the script directory; the stdlib-only setup module beside this file is app-owned.
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hardware  # noqa: E402
import inference_setup  # noqa: E402

WIRE_OUT = sys.stdout
OUTPUT_LOCK = threading.Lock()


def emit(kind, payload):
    with OUTPUT_LOCK:
        print(json.dumps({"kind": kind, "payload": payload}), file=WIRE_OUT, flush=True)


def fetch_model_file(download, manifest, entry, model):
    stop = threading.Event()
    def observe_bytes():
        directory = model / ".cache/huggingface/download" / Path(entry["path"]).parent
        while not stop.wait(0.5):
            try:
                completed = sum(path.stat().st_size for path in directory.glob("*." + entry["sha256"] + ".incomplete"))
                if completed:
                    progress("Downloading " + entry["path"], min(completed, entry["size"]), entry["size"], "bytes")
            except OSError:
                pass  # A completed download can rename its partial file during observation.
    observer = threading.Thread(target=observe_bytes, daemon=True)
    observer.start()
    try:
        download(manifest["repoId"], entry["path"], revision=manifest["revision"], local_dir=model, token=False)
    finally:
        stop.set()
        observer.join(timeout=2)


def progress(stage, completed=0, total=0, unit="steps"):
    emit("progress", dict(stage=stage, completed=completed, total=total, unit=unit))


def save_artifact(data_root, job_id, image, result):
    if len(job_id) != 32 or any(c not in "0123456789abcdef" for c in job_id):
        raise ValueError("Invalid artifact identifier")
    directory = data_root / "artifacts" / job_id
    directory.mkdir(parents=True, exist_ok=True)  # The coordinator may already hold the input picture here.
    temporary = directory / "image.partial"
    image.save(temporary, format="PNG")
    target = directory / "image.png"
    os.replace(temporary, target)
    # Stored relative to artifacts/ so the data folder can move; the coordinator resolves it.
    result.update(imagePath=f"{job_id}/image.png", imageSha256=hashlib.sha256(target.read_bytes()).hexdigest())


def enlarge(payload, data_root, weights, gpu, torch, upscaler):
    from PIL import Image, ImageOps
    with Image.open(payload["sourceImage"]) as opened:
        picture = ImageOps.exif_transpose(opened).convert("RGB")
    if max(picture.size) > inference_setup.UPSCALE_MAX_SIDE:
        raise ValueError(f"Pictures up to {inference_setup.UPSCALE_MAX_SIDE} pixels on the longest side can be upscaled; "
                         f"this one is {picture.width} × {picture.height}.")
    progress("Loading the upscaler")
    loading = time.monotonic()
    model = upscaler.load(weights, "cuda")
    started = time.monotonic()
    # Larger tiles are faster; the size follows the graphics memory free now, and shrinks if it still runs short.
    tile = hardware.upscale_tile(torch.cuda.mem_get_info(0)[0] / hardware.MIB)
    while True:
        try:
            image = upscaler.upscale(model, picture, "cuda", tile=tile,
                                     progress=lambda done, total: progress("Enlarging picture", done, total, "tiles"))
            break
        except torch.cuda.OutOfMemoryError:
            if tile <= hardware.UPSCALE_MIN_TILE:
                raise RuntimeError("The graphics card ran out of memory while enlarging. Close other programs that use it and try again.") from None
            torch.cuda.empty_cache()
            tile = max(hardware.UPSCALE_MIN_TILE, tile // 2)
            progress(f"Graphics memory is short; using {tile}-pixel tiles")
    if payload["scale"] == 2:
        image = image.resize((picture.width * 2, picture.height * 2), Image.Resampling.LANCZOS)
    result = dict(gpu=gpu, torch=torch.__version__, cuda=torch.version.cuda, precision="float32", tile=tile,
        seconds=round(time.monotonic() - started, 2), loadSeconds=round(started - loading, 2),
        width=image.width, height=image.height, scale=payload["scale"],
        peakVramMiB=round(torch.cuda.max_memory_allocated() / 1024**2), python=sys.version.split()[0])
    save_artifact(data_root, payload["jobId"], image, result)
    return result


def painted_change(payload):
    """The starting picture, its painted area at full size, and the region the model redraws."""
    from PIL import Image, ImageOps
    with Image.open(payload["sourceImage"]) as opened:
        picture = ImageOps.exif_transpose(opened).convert("RGB")
    if max(picture.size) > inference_setup.EDIT_MAX_SIDE:
        raise ValueError(f"Pictures up to {inference_setup.EDIT_MAX_SIDE} pixels on the longest side can be changed by painting; "
                         f"this one is {picture.width} × {picture.height}. Make a smaller copy first.")
    with Image.open(payload["maskImage"]) as opened:
        cells = opened.convert("L")
    if not inference_setup.mask_matches(cells.size, picture.size):
        raise ValueError("The painted area does not match this picture. Paint it again.")
    mask = cells.resize(picture.size, Image.Resampling.BILINEAR).point(lambda value: 255 if value > 127 else 0)
    bounds = mask.getbbox()
    if bounds is None:
        raise ValueError("Paint over the part of the picture you want to change.")
    box, shape = inference_setup.edit_region(bounds, picture.size)
    return picture, mask, box, shape


def blend_change(picture, mask, box, drawn):
    """Pastes the redrawn region back with a softened edge; unpainted pixels keep their original values."""
    from PIL import Image, ImageFilter
    size = (box[2] - box[0], box[3] - box[1])
    region = drawn.resize(size, Image.Resampling.LANCZOS)
    feather = max(2, round(max(size) / 96))
    edge = mask.crop(box).filter(ImageFilter.MaxFilter(2 * feather + 1)).filter(ImageFilter.GaussianBlur(feather))
    result = picture.copy()
    result.paste(region, box[:2], edge)
    return result


def load_added(data_root, payload, torch, dtype):
    """An added SD 1.5 model with the bundled tokenizer, scheduler, and safety checker (and VAE if it has none).

    Weights load only from safetensors in the app's own copy; every configuration comes from the
    bundled Stable Diffusion 1.5 files, never from the user's model, and nothing is fetched online.
    """
    from diffusers import AutoencoderKL, PNDMScheduler, StableDiffusionPipeline, UNet2DConditionModel
    from diffusers.pipelines.stable_diffusion.safety_checker import StableDiffusionSafetyChecker
    from transformers import CLIPImageProcessor, CLIPTextModel, CLIPTokenizer
    model_id = payload["model"]
    if len(model_id) != 32 or any(c not in "0123456789abcdef" for c in model_id):
        raise ValueError("Invalid model identifier")
    bundled = str(data_root / "models" / inference_setup.RECIPE)
    folder = data_root / "models" / "imported" / model_id
    weights = dict(torch_dtype=dtype, use_safetensors=True, local_files_only=True)
    parts = dict(tokenizer=CLIPTokenizer.from_pretrained(bundled, subfolder="tokenizer", local_files_only=True),
                 scheduler=PNDMScheduler.from_pretrained(bundled, subfolder="scheduler", local_files_only=True),
                 feature_extractor=CLIPImageProcessor.from_pretrained(bundled, subfolder="feature_extractor", local_files_only=True),
                 safety_checker=StableDiffusionSafetyChecker.from_pretrained(bundled, subfolder="safety_checker", variant="fp16", **weights))
    if not payload["ownVae"]:
        parts["vae"] = AutoencoderKL.from_pretrained(bundled, subfolder="vae", variant="fp16", **weights)
    if payload["modelFormat"] == "single-file":
        return StableDiffusionPipeline.from_single_file(str(folder / "model.safetensors"), config=bundled,
            torch_dtype=dtype, local_files_only=True, **parts)
    if payload["modelFormat"] != "diffusers":
        raise ValueError("Unknown model format")
    if payload["ownVae"]:
        parts["vae"] = AutoencoderKL.from_pretrained(str(folder / "vae"), **weights)
    return StableDiffusionPipeline(unet=UNet2DConditionModel.from_pretrained(str(folder / "unet"), **weights),
        text_encoder=CLIPTextModel.from_pretrained(str(folder / "text_encoder"), **weights), **parts)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--operation", choices=["download", "verify", "verify-model", "generate", "upscale"], required=True)
    parser.add_argument("--payload", required=True)
    args = parser.parse_args()
    if not sys.flags.isolated or not sys.flags.no_user_site:
        raise RuntimeError("The inference interpreter must run in isolated mode (-I).")
    payload = json.loads(args.payload)
    manifest = json.loads((args.root / "profiles/sd15-files.json").read_text())
    model = args.data_root / "models/sd15-baseline"
    with redirect_stdout(sys.stderr):
        if args.operation == "download":
            # Downloads use the supported Hub cache; generation itself is strictly offline.
            os.environ.pop("HF_HUB_OFFLINE", None)
            os.environ.pop("TRANSFORMERS_OFFLINE", None)
            os.environ["HF_HUB_DISABLE_XET"] = "1"
            os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
            from huggingface_hub import hf_hub_download
            for i, entry in enumerate(manifest["files"]):
                progress("Fetching " + entry["path"], i, len(manifest["files"]), "files")
                fetch_model_file(hf_hub_download, manifest, entry, model)
            emit("result", {"downloaded": True})
            return
        progress("Loading inference runtime")
        launched = time.monotonic()
        import torch
        import upscaler
        from diffusers import (DPMSolverMultistepScheduler, EulerAncestralDiscreteScheduler,
                               StableDiffusionImg2ImgPipeline, StableDiffusionInpaintPipeline, StableDiffusionPipeline)
        from PIL import Image, ImageFilter, ImageOps, ImageStat
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is unavailable. Check your NVIDIA driver; this profile requires an NVIDIA GPU.")
        # The parent sets CUDA_DEVICE_ORDER=PCI_BUS_ID, so device 0 is the GPU its preflight inspected.
        gpu = torch.cuda.get_device_name(0)
        minimum = inference_setup.minimum_vram_mib(args.root)
        total = torch.cuda.get_device_properties(0).total_memory / 1024**2
        if not inference_setup.meets_vram(total, minimum):
            raise RuntimeError(f"{gpu} reports {total:.0f} MiB of GPU memory; this recipe requires {minimum} MiB.")
        upscaler_weights = args.data_root / "models" / inference_setup.UPSCALER / "RealESRGAN_x4plus.pth"
        # Starting the runtime, loading the model, and the work itself are timed apart, so a slow drive or a busy
        # computer can be told from slow inference.
        startup = round(time.monotonic() - launched, 2)
        if args.operation == "upscale":
            emit("result", dict(enlarge(payload, args.data_root, upscaler_weights, gpu, torch, upscaler), startupSeconds=startup))
            return
        # The coordinator sends the runtime choice that passed this installation's (or this model's) GPU test, or the
        # one under test. Check it again against the card CUDA sees, so a swapped GPU cannot run untested settings.
        runtime = payload.get("runtime")
        if runtime not in hardware.RUNTIMES:
            raise ValueError("Unknown runtime choice")
        major, minor = torch.cuda.get_device_capability(0)
        if not hardware.allows(runtime, dict(name=gpu, computeCapability=f"{major}.{minor}", vramMiB=total)):
            raise RuntimeError(f"The settings tested for this installation do not suit {gpu}. Install / verify again in Create.")
        settings = hardware.RUNTIMES[runtime]
        # FP16 is used only where hardware.half_precision_ok trusts it; GTX 16-series cards stay in FP32.
        dtype = torch.float16 if settings["precision"] == "float16" else torch.float32
        source = payload.get("sourceImage") if args.operation == "generate" else None
        painted = painted_change(payload) if source and payload.get("maskImage") else None  # Checked before the slow load.
        added = payload.get("model", inference_setup.RECIPE) != inference_setup.RECIPE
        progress("Loading your model" if added else "Loading Stable Diffusion 1.5")
        loading = time.monotonic()
        # The same weights serve every mode: image-to-image starts from the user's picture, and a painted
        # change redraws only the painted region (Diffusers blends the standard 4-channel UNet's latents).
        pipeline_class = (StableDiffusionInpaintPipeline if painted else StableDiffusionImg2ImgPipeline) if source else StableDiffusionPipeline
        if added:
            pipe = load_added(args.data_root, payload, torch, dtype)
            if pipeline_class is not StableDiffusionPipeline:
                pipe = pipeline_class.from_pipe(pipe)
        else:
            pipe = pipeline_class.from_pretrained(str(model), torch_dtype=dtype,
                variant="fp16", use_safetensors=True, local_files_only=True)
        if settings["offload"]:
            pipe.enable_model_cpu_offload()  # Each part moves to the GPU only while it runs.
        else:
            pipe.to("cuda")
        pipe.enable_vae_slicing()
        pipe.set_progress_bar_config(disable=True)
        loaded = round(time.monotonic() - loading, 2)
        request = payload if args.operation == "generate" else dict(
            prompt="A small red ceramic house beside a green tree, watercolor illustration, daylight",
            negativePrompt="", seed=20261017, steps=4, width=512, height=512)
        # The coordinator validated the sampler; the model's own PNDM scheduler is the default.
        sampler = request.get("sampler", "pndm")
        if sampler == "dpmpp-2m":
            pipe.scheduler = DPMSolverMultistepScheduler.from_config(pipe.scheduler.config,
                algorithm_type="dpmsolver++", use_karras_sigmas=True)
        elif sampler == "euler-a":
            pipe.scheduler = EulerAncestralDiscreteScheduler.from_config(pipe.scheduler.config)
        elif sampler != "pndm":
            raise ValueError("Unknown sampler: " + str(sampler)[:40])
        width, height = request.get("width", 512), request.get("height", 512)
        extra = {}
        if painted:
            picture, mask, box, (width, height) = painted
            # A slightly grown mask lets the model redraw the edge it will be blended across.
            region_mask = mask.crop(box).resize((width, height), Image.Resampling.BILINEAR).filter(ImageFilter.MaxFilter(9))
            extra = dict(image=picture.crop(box).resize((width, height), Image.Resampling.LANCZOS), mask_image=region_mask,
                         width=width, height=height, strength=request["strength"])
        elif source:
            with Image.open(source) as opened:
                picture = ImageOps.exif_transpose(opened).convert("RGB")
            width, height = inference_setup.shape_for(*picture.size)
            extra = dict(image=ImageOps.fit(picture, (width, height), Image.Resampling.LANCZOS), strength=request["strength"])
        else:
            extra = dict(width=width, height=height)
        def step_end(pipeline, step, timestep, callback_kwargs):
            progress("Generating image", step + 1, getattr(pipeline, "num_timesteps", None) or len(pipeline.scheduler.timesteps))
            return callback_kwargs
        started = time.monotonic()
        output = pipe(prompt=request["prompt"], negative_prompt=request["negativePrompt"],
            num_inference_steps=request["steps"], **extra,
            guidance_scale=request.get("guidanceScale", 7.5), generator=torch.Generator(device="cpu").manual_seed(request["seed"]),
            callback_on_step_end=step_end)
        image = output.images[0]
        blocked = bool(output.nsfw_content_detected and output.nsfw_content_detected[0])
        if blocked:
            raise RuntimeError("The model safety checker rejected the result. Try a different prompt.")
        if image.size != (width, height) or max(ImageStat.Stat(image).stddev) < 1:
            raise RuntimeError("Inference produced an empty or invalid image; the installation is not verified.")
        if painted:
            image = blend_change(picture, mask, box, image)
            width, height = image.size
        if args.operation == "verify":
            # The upscaler shares this runtime; a small real enlargement proves it loads and runs here.
            progress("Testing the picture upscaler")
            model = upscaler.load(upscaler_weights, "cuda")
            enlarged = upscaler.upscale(model, image.crop((0, 0, 64, 64)), "cuda")
            if enlarged.size != (256, 256) or max(ImageStat.Stat(enlarged).stddev) < 1:
                raise RuntimeError("The upscaler produced an invalid image; the installation is not verified.")
            del model
        result = dict(gpu=gpu, torch=torch.__version__, cuda=torch.version.cuda,
            precision=settings["precision"], offload=settings["offload"], runtime=runtime,
            seconds=round(time.monotonic() - started, 2), loadSeconds=loaded, startupSeconds=startup,
            width=width, height=height, sampler=sampler,
            peakVramMiB=round(torch.cuda.max_memory_allocated() / 1024**2),
            python=sys.version.split()[0], isolated=bool(sys.flags.isolated), userSiteDisabled=bool(sys.flags.no_user_site))
        if args.operation == "generate":
            save_artifact(args.data_root, payload["jobId"], image, result)
        emit("result", result)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        emit("error", {"message": str(exc)[:2000]})
        raise SystemExit(1)
