"""Opt-in real GPU check for Stable Diffusion XL: setup, a real picture at its size, and a painted change.

Usage (from the repository, after scripts/build.ps1):
    runtime/python/python.exe tests/gpu_xl_check.py <data folder> [--expect <xl runtime>] [--steps N]

Needs a verified Stable Diffusion 1.5 installation in the folder (run gpu_hardware_profile_check.py or
gpu_video_check.py first). Set up Stable Diffusion XL downloads the pinned SDXL base 1.0 files (6.9 GB),
verifies them, and makes a 1024 x 1024 test picture on the GPU. The check then makes a landscape picture
and changes a painted rectangle of it. Not part of check.ps1.
"""
import json
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from gpu_model_import_check import grayscale_png  # noqa: E402


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    data = sys.argv[1]
    expected = sys.argv[sys.argv.index("--expect") + 1] if "--expect" in sys.argv else None
    steps = int(sys.argv[sys.argv.index("--steps") + 1]) if "--steps" in sys.argv else 20
    worker = subprocess.Popen([sys.executable, "-I", str(ROOT / "workers/python/media_worker.py"), "--root", str(ROOT)],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
    if json.loads(worker.stdout.readline())["kind"] != "hello":
        raise SystemExit("The worker did not start.")

    def call(operation, payload):
        request = uuid.uuid4().hex
        worker.stdin.write(json.dumps({"protocol": 1, "id": request, "operation": operation, "payload": payload}) + "\n")
        worker.stdin.flush()
        stage = None
        while True:
            event = json.loads(worker.stdout.readline())
            if event["kind"] == "progress" and event["payload"].get("stage") != stage:
                stage = event["payload"].get("stage")
                print("  ", stage, flush=True)
            elif event["kind"] == "result":
                return event["payload"]
            elif event["kind"] == "error":
                raise SystemExit(f"FAIL: {operation}: {event['payload']['message']}")

    try:
        ready = call("xl.install", {"dataRoot": data})
        record = json.loads((Path(data) / "jobs/install-sdxl.json").read_text(encoding="utf-8"))
        evidence = record["evidence"]
        print(f"PASS: SDXL {ready['status']} with {record['runtime']} ({evidence['precision']}, {evidence['offload']} offload): "
              f"1024 x 1024 4-step test {evidence['seconds']} s after loading {evidence.get('loadSeconds')} s, peak {evidence['peakVramMiB']} MiB. {ready['message']}")
        if expected and record["runtime"] != expected:
            raise SystemExit(f"FAIL: expected {expected}, SDXL was verified with {record['runtime']}")
        picture = call("generate.image", dict(dataRoot=data, prompt="A lighthouse on a rocky coast at sunset, oil painting",
                                              seed=20261018, steps=steps, model="sdxl-base", width=1216, height=832))
        print(f"PASS: SDXL picture {picture['width']}x{picture['height']}, {steps} steps in {picture['seconds']} s after loading "
              f"{picture.get('loadSeconds')} s, peak {picture['peakVramMiB']} MiB")
        mask = Path(data).parent / f"media-workbench-xl-mask-{uuid.uuid4().hex}.png"
        # The painted-area grid has the picture's proportions at most 512 cells a side: 512 x 350 for 1216 x 832.
        grayscale_png(mask, 512, 350, (280, 50, 440, 170))
        try:
            changed = call("generate.image", dict(dataRoot=data, prompt="a full moon in the sky", seed=7, steps=steps, model="sdxl-base",
                                                  width=1024, height=1024, sourceImage=picture["imagePath"], maskImage=str(mask), strength=1.0))
        finally:
            mask.unlink(missing_ok=True)
        print(f"PASS: SDXL painted change kept the picture's size {changed['width']}x{changed['height']} in {changed['seconds']} s")
    finally:
        worker.stdin.close()
        worker.wait(timeout=30)


if __name__ == "__main__":
    main()
