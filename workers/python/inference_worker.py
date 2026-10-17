"""Private Python 3.12 ML child. Only the bundled recipe is executable."""
import argparse
from contextlib import redirect_stdout
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
import time

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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--operation", choices=["download", "verify", "generate"], required=True)
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
        import torch
        from diffusers import StableDiffusionPipeline
        from PIL import ImageStat
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is unavailable. Check your NVIDIA driver; this profile requires an NVIDIA GPU.")
        gpu = torch.cuda.get_device_name(0)
        if torch.cuda.get_device_properties(0).total_memory < 5.5 * 1024**3:
            raise RuntimeError("This baseline requires at least 6 GB of GPU memory.")
        # FP32 avoids known half-precision limitations on GTX 16-series devices.
        progress("Loading Stable Diffusion 1.5")
        pipe = StableDiffusionPipeline.from_pretrained(str(model), torch_dtype=torch.float32,
            variant="fp16", use_safetensors=True, local_files_only=True)
        pipe.enable_model_cpu_offload()
        pipe.enable_vae_slicing()
        pipe.set_progress_bar_config(disable=True)
        settings = payload if args.operation == "generate" else dict(
            prompt="A small red ceramic house beside a green tree, watercolor illustration, daylight",
            negativePrompt="", seed=20261017, steps=4, width=512, height=512)
        def step_end(pipeline, step, timestep, callback_kwargs):
            progress("Generating image", step + 1, len(pipeline.scheduler.timesteps))
            return callback_kwargs
        started = time.monotonic()
        output = pipe(prompt=settings["prompt"], negative_prompt=settings["negativePrompt"],
            width=settings["width"], height=settings["height"], num_inference_steps=settings["steps"],
            guidance_scale=settings.get("guidanceScale", 7.5), generator=torch.Generator(device="cpu").manual_seed(settings["seed"]),
            callback_on_step_end=step_end)
        image = output.images[0]
        blocked = bool(output.nsfw_content_detected and output.nsfw_content_detected[0])
        if blocked:
            raise RuntimeError("The model safety checker rejected the result. Try a different prompt.")
        if image.size != (512, 512) or max(ImageStat.Stat(image).stddev) < 1:
            raise RuntimeError("Inference produced an empty or invalid image; the installation is not verified.")
        result = dict(gpu=gpu, torch=torch.__version__, cuda=torch.version.cuda,
            precision="float32", seconds=round(time.monotonic() - started, 2), width=512, height=512,
            peakVramMiB=round(torch.cuda.max_memory_allocated() / 1024**2),
            python=sys.version.split()[0], isolated=bool(sys.flags.isolated), userSiteDisabled=bool(sys.flags.no_user_site))
        if args.operation == "generate":
            job_id = payload["jobId"]
            if len(job_id) != 32 or any(c not in "0123456789abcdef" for c in job_id):
                raise ValueError("Invalid artifact identifier")
            directory = args.data_root / "artifacts" / job_id
            directory.mkdir(parents=True, exist_ok=False)
            temporary = directory / "image.partial"
            image.save(temporary, format="PNG")
            target = directory / "image.png"
            os.replace(temporary, target)
            result.update(imagePath=str(target), imageSha256=hashlib.sha256(target.read_bytes()).hexdigest())
        emit("result", result)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        emit("error", {"message": str(exc)[:2000]})
        raise SystemExit(1)
