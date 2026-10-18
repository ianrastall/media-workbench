"""Opt-in real GPU check: add a local SD 1.5 model, use it in every Create mode, then remove it.

Usage (from the repository, after scripts/build.ps1):
    runtime/python/python.exe tests/gpu_model_import_check.py <data folder with verified SD 1.5> <model file or folder> [--keep]

The data folder must already pass Install / verify. This copies the model (several GiB) into that
folder and runs real inference through the same worker the app uses; it is not part of check.ps1.
Use only a model you are licensed to use; nothing is downloaded.
"""
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import uuid
import zlib

ROOT = Path(__file__).resolve().parents[1]


def grayscale_png(path, width, height, painted):
    """A painted-area PNG: white inside the (left, top, right, bottom) box."""
    left, top, right, bottom = painted
    rows = b"".join(b"\0" + bytes(255 if left <= x < right and top <= y < bottom else 0 for x in range(width)) for y in range(height))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    data, source, keep = sys.argv[1], sys.argv[2], "--keep" in sys.argv
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
        found = call("models.inspect", {"path": source})
        print("PASS: identified", found["format"], found["summary"])
        added = call("models.import", {"dataRoot": data, "path": source, "name": "GPU check " + Path(source).stem})
        if added["status"] != "ready":
            raise SystemExit(f"FAIL: added model is {added['status']}: {added['message']}")
        print(f"PASS: added and tested {added['name']} ({added['id']})")
        listed = {model["id"]: model["status"] for model in call("models.list", {"dataRoot": data})["models"]}
        if listed.get(added["id"]) != "ready":
            raise SystemExit("FAIL: the added model is not listed ready")
        prompt = dict(dataRoot=data, prompt="A lighthouse on a rocky coast at sunset, oil painting", seed=20261017, steps=20, model=added["id"])
        text = call("generate.image", prompt)
        print(f"PASS: text to image {text['width']}x{text['height']} in {text['seconds']}s, peak {text['peakVramMiB']} MiB on {text['gpu']}")
        if text["settings"]["model"] != added["id"] or text["modelName"] != added["name"]:
            raise SystemExit("FAIL: the job record does not name the model that made it")
        changed = call("generate.image", dict(prompt, prompt="A lighthouse on a rocky coast in winter snow", sourceImage=text["imagePath"], strength=0.6))
        print(f"PASS: image to image in {changed['seconds']}s")
        with tempfile.TemporaryDirectory() as folder:
            mask = Path(folder) / "mask.png"
            grayscale_png(mask, 512, 512, (160, 96, 352, 288))
            painted = call("generate.image", dict(prompt, prompt="A full moon in a clear sky", sourceImage=text["imagePath"], maskImage=str(mask), strength=1.0))
        print(f"PASS: painted change in {painted['seconds']}s")
        bigger = call("upscale.image", {"dataRoot": data, "sourceImage": text["imagePath"], "scale": 2})
        print(f"PASS: upscaled to {bigger['width']}x{bigger['height']}")
        for image in (text, changed, painted, bigger):
            print("   ", image["imagePath"])
        if not keep:
            remaining = call("models.remove", {"dataRoot": data, "modelId": added["id"]})["models"]
            if any(model["id"] == added["id"] for model in remaining) or not Path(text["imagePath"]).is_file():
                raise SystemExit("FAIL: removal did not keep images or did not remove the model")
            print("PASS: removed the added model; images were kept")
    finally:
        worker.stdin.close()
        worker.wait(timeout=30)


if __name__ == "__main__":
    main()
