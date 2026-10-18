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

inference_setup.pin_temporary_folder()

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


def painted_change(payload, xl=False):
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
    box, shape = inference_setup.edit_region(bounds, picture.size, xl=xl)
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


def safety_checker(data_root, torch):
    """Stable Diffusion 1.5's safety checker on the CPU, for pipelines that have none of their own (videos, SDXL)."""
    from diffusers.pipelines.stable_diffusion.safety_checker import StableDiffusionSafetyChecker
    from transformers import CLIPImageProcessor
    bundled = str(data_root / "models" / inference_setup.RECIPE)
    checker = StableDiffusionSafetyChecker.from_pretrained(bundled, subfolder="safety_checker", variant="fp16",
        torch_dtype=torch.float32, use_safetensors=True, local_files_only=True)
    return checker, CLIPImageProcessor.from_pretrained(bundled, subfolder="feature_extractor", local_files_only=True)


def flagged(checker, extractor, pictures, torch):
    """Whether the safety checker rejects any of these RGB pictures."""
    import numpy
    arrays = numpy.stack([numpy.asarray(picture, dtype=numpy.float32) / 255 for picture in pictures])
    with torch.no_grad():
        clip = extractor(pictures, return_tensors="pt").pixel_values.to(torch.float32)
        _, found = checker(images=arrays, clip_input=clip)
    return any(found)


def place(pipe, offload):
    """Moves a pipeline onto the GPU as the verified runtime choice says."""
    if offload == "sequential":
        pipe.enable_sequential_cpu_offload()  # Streams layers to the GPU; fits 6 GB in full precision.
    elif offload:
        pipe.enable_model_cpu_offload()  # Each part moves to the GPU only while it runs.
    else:
        pipe.to("cuda")


def video_folder(args, payload):
    """Where the child writes a clip: its job's artifact folder, or setup's test folder under cache/."""
    if args.operation == "video":
        job_id = payload["jobId"]
        if len(job_id) != 32 or any(c not in "0123456789abcdef" for c in job_id):
            raise ValueError("Invalid artifact identifier")
        folder = args.data_root / "artifacts" / job_id
    else:
        folder = Path(payload["outputFolder"]).resolve()
        if not folder.is_relative_to((args.data_root / "cache" / "video-test").resolve()):
            raise ValueError("The video test must write inside the data folder's cache")
    folder.mkdir(parents=True, exist_ok=True)
    return folder


def make_video(args, payload, torch, gpu, total, startup):
    """A short AnimateDiff clip from the verified SD 1.5 weights: raw frames and a poster, checked frame by frame."""
    import numpy
    from diffusers import AnimateDiffPipeline, DDIMScheduler, MotionAdapter, StableDiffusionPipeline
    from PIL import ImageStat
    runtime = payload.get("runtime")
    if runtime not in hardware.VIDEO_RUNTIMES:
        raise ValueError("Unknown video runtime choice")
    major, minor = torch.cuda.get_device_capability(0)
    if not hardware.allows(runtime, dict(name=gpu, computeCapability=f"{major}.{minor}", vramMiB=total), hardware.VIDEO_RUNTIMES):
        raise RuntimeError(f"The video settings tested here do not suit {gpu}. Select Set up videos again.")
    settings = hardware.VIDEO_RUNTIMES[runtime]
    dtype = torch.float16 if settings["precision"] == "float16" else torch.float32
    folder = video_folder(args, payload)
    frames, width, height = payload["frames"], payload["width"], payload["height"]
    progress("Loading Stable Diffusion 1.5 and its motion layers")
    loading = time.monotonic()
    base = StableDiffusionPipeline.from_pretrained(str(args.data_root / "models" / inference_setup.RECIPE), torch_dtype=dtype,
        variant="fp16", use_safetensors=True, local_files_only=True)
    adapter = MotionAdapter.from_pretrained(str(args.data_root / "models" / "animatediff-v1-5-3"), torch_dtype=dtype,
        use_safetensors=True, local_files_only=True)
    # AnimateDiff has no safety checker of its own; the model's checker reviews every frame on the CPU.
    checker, extractor = base.safety_checker.to("cpu", torch.float32), base.feature_extractor
    pipe = AnimateDiffPipeline.from_pipe(base, motion_adapter=adapter)
    del base
    # The scheduler settings AnimateDiff was trained with.
    pipe.scheduler = DDIMScheduler.from_config(pipe.scheduler.config, beta_schedule="linear", clip_sample=False,
        timestep_spacing="linspace", steps_offset=1)
    place(pipe, settings["offload"])
    pipe.enable_vae_slicing()
    pipe.set_progress_bar_config(disable=True)
    loaded = round(time.monotonic() - loading, 2)

    def step_end(pipeline, step, timestep, callback_kwargs):
        progress("Making the video", step + 1, getattr(pipeline, "num_timesteps", None) or len(pipeline.scheduler.timesteps))
        return callback_kwargs
    started = time.monotonic()
    try:
        output = pipe(prompt=payload["prompt"], negative_prompt=payload["negativePrompt"], num_frames=frames,
            width=width, height=height, num_inference_steps=payload["steps"], guidance_scale=payload.get("guidanceScale", 7.5),
            generator=torch.Generator(device="cpu").manual_seed(payload["seed"]), callback_on_step_end=step_end)
    except torch.cuda.OutOfMemoryError:
        raise RuntimeError("The graphics card ran out of memory making this video. Try the square shape or the shorter "
                           "length, and close other programs that use the graphics card.") from None
    seconds = round(time.monotonic() - started, 2)
    pictures = [picture.convert("RGB") for picture in output.frames[0]]
    if len(pictures) != frames or any(picture.size != (width, height) for picture in pictures) \
            or max(ImageStat.Stat(pictures[0]).stddev) < 1:
        raise RuntimeError("Inference produced an empty or invalid video; it was not saved.")
    progress("Checking the frames")
    rejected = flagged(checker, extractor, pictures, torch)
    # The setup test only proves the runtime: its 4-step frames are unfinished texture the checker can misread.
    if rejected and args.operation == "video":
        raise RuntimeError("The model safety checker rejected part of the video. Try a different prompt.")
    raw = folder / "frames.rgb.partial"
    with raw.open("wb") as stream:
        for picture in pictures:
            stream.write(numpy.asarray(picture, dtype=numpy.uint8).tobytes())
    os.replace(raw, folder / "frames.rgb")
    poster = folder / "poster.partial"
    pictures[0].save(poster, format="PNG")
    os.replace(poster, folder / "poster.png")
    result = dict(gpu=gpu, torch=torch.__version__, cuda=torch.version.cuda, precision=settings["precision"],
        offload=settings["offload"], runtime=runtime, seconds=seconds, loadSeconds=loaded, startupSeconds=startup,
        width=width, height=height, frames=frames, sampler="ddim", safetyChecked=True, safetyFlagged=rejected,
        peakVramMiB=round(torch.cuda.max_memory_allocated() / 1024**2), python=sys.version.split()[0])
    if args.operation == "video":
        target = folder / "poster.png"
        result.update(imagePath=f"{payload['jobId']}/poster.png", imageSha256=hashlib.sha256(target.read_bytes()).hexdigest())
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--operation", choices=["download", "verify", "verify-model", "generate", "upscale", "video", "verify-video", "verify-xl"], required=True)
    parser.add_argument("--payload", required=True)
    args = parser.parse_args()
    if not sys.flags.isolated or not sys.flags.no_user_site:
        raise RuntimeError("The inference interpreter must run in isolated mode (-I).")
    payload = json.loads(args.payload)
    # Downloads name one of the pinned manifests; each has its own folder under models/.
    downloads = {"sd15-files.json": inference_setup.RECIPE, "sdxl-files.json": inference_setup.XL_MODEL}
    chosen = payload.get("manifest", "sd15-files.json") if args.operation == "download" else "sd15-files.json"
    if chosen not in downloads:
        raise ValueError("Unknown model manifest")
    manifest = json.loads((args.root / "profiles" / chosen).read_text())
    model = args.data_root / "models" / downloads[chosen]
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
        if args.operation in ("video", "verify-video"):
            emit("result", make_video(args, payload, torch, gpu, total, startup))
            return
        # The coordinator sends the runtime choice that passed this installation's (or this model's) GPU test, or the
        # one under test. Check it again against the card CUDA sees, so a swapped GPU cannot run untested settings.
        # Stable Diffusion XL has its own verified runtime choices; everything else uses SD 1.5's.
        xl = args.operation == "verify-xl" or payload.get("model") == inference_setup.XL_MODEL
        table = hardware.XL_RUNTIMES if xl else hardware.RUNTIMES
        runtime = payload.get("runtime")
        if runtime not in table:
            raise ValueError("Unknown runtime choice")
        major, minor = torch.cuda.get_device_capability(0)
        if not hardware.allows(runtime, dict(name=gpu, computeCapability=f"{major}.{minor}", vramMiB=total), table):
            raise RuntimeError(f"The settings tested for this installation do not suit {gpu}. Install / verify again in Create.")
        settings = table[runtime]
        # FP16 is used only where hardware.half_precision_ok trusts it; GTX 16-series cards stay in FP32.
        dtype = torch.float16 if settings["precision"] == "float16" else torch.float32
        source = payload.get("sourceImage") if args.operation == "generate" else None
        painted = painted_change(payload, xl) if source and payload.get("maskImage") else None  # Checked before the slow load.
        added = not xl and payload.get("model", inference_setup.RECIPE) != inference_setup.RECIPE
        progress("Loading Stable Diffusion XL" if xl else "Loading your model" if added else "Loading Stable Diffusion 1.5")
        loading = time.monotonic()
        # The same weights serve every mode: image-to-image starts from the user's picture, and a painted
        # change redraws only the painted region (Diffusers blends the standard 4-channel UNet's latents).
        pipeline_class = (StableDiffusionInpaintPipeline if painted else StableDiffusionImg2ImgPipeline) if source else StableDiffusionPipeline
        checker = None
        if xl:
            from diffusers import StableDiffusionXLImg2ImgPipeline, StableDiffusionXLInpaintPipeline, StableDiffusionXLPipeline
            pipe = StableDiffusionXLPipeline.from_pretrained(str(args.data_root / "models" / inference_setup.XL_MODEL), torch_dtype=dtype,
                variant="fp16", use_safetensors=True, local_files_only=True, add_watermarker=False)
            if source:
                pipe = (StableDiffusionXLInpaintPipeline if painted else StableDiffusionXLImg2ImgPipeline).from_pipe(pipe)
            checker = safety_checker(args.data_root, torch)  # SDXL has none of its own.
        elif added:
            pipe = load_added(args.data_root, payload, torch, dtype)
            if pipeline_class is not StableDiffusionPipeline:
                pipe = pipeline_class.from_pipe(pipe)
        else:
            pipe = pipeline_class.from_pretrained(str(model), torch_dtype=dtype,
                variant="fp16", use_safetensors=True, local_files_only=True)
        place(pipe, settings["offload"])
        pipe.enable_vae_slicing()
        if xl:
            pipe.enable_vae_tiling()  # Decoding 1024 pixels in full precision would not fit 6 GB in one piece.
        pipe.set_progress_bar_config(disable=True)
        loaded = round(time.monotonic() - loading, 2)
        request = payload if args.operation in ("generate", "verify-xl") else dict(
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
            width, height = inference_setup.shape_for(*picture.size, xl=xl)
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
        if checker is not None:
            progress("Checking the picture")
        blocked = flagged(*checker, [image.convert("RGB")], torch) if checker is not None \
            else bool(output.nsfw_content_detected and output.nsfw_content_detected[0])
        # The SDXL setup test only proves the runtime; its 4-step picture is unfinished and can be misread.
        if blocked and args.operation != "verify-xl":
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
