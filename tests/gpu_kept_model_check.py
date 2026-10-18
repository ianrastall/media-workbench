"""Opt-in real GPU check: a kept-loaded picture model makes exactly the pictures a fresh one makes, faster.

Usage (from the repository, with a data folder whose Stable Diffusion 1.5 is already verified):
    runtime/python/python.exe tests/gpu_kept_model_check.py <data folder>

Makes one picture with a one-shot coordinator (the model is loaded and dropped), then the same seed and settings
several times through a coordinator that keeps the model loaded, with a sampler change and an image-to-image
picture in between. Every repeat of the original settings must be byte-identical to the one-shot picture (nothing
leaks between pictures), repeats must reuse the loaded model, and while it is loaded another program must find
the data folder busy. Adds a handful of pictures to that folder's Library; it is not part of check.ps1.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
PICTURE = dict(prompt="A red ceramic house beside a green tree, watercolor illustration, daylight", seed=20261017, steps=12)
OTHER_OWNER = ("import sys; sys.path.insert(0, sys.argv[1]); import inference_setup as s; from pathlib import Path\n"
               "try:\n    with s.exclusive(Path(sys.argv[2]).resolve()): print('locked')\nexcept s.SetupError: print('busy')")


class Coordinator:
    def __init__(self, keep):
        environment = dict(os.environ, PYTHONUTF8="1")
        environment.pop("MEDIA_WORKBENCH_KEEP_MODEL", None)
        if keep:
            environment["MEDIA_WORKBENCH_KEEP_MODEL"] = "1"
        self.worker = subprocess.Popen([sys.executable, "-I", str(ROOT / "workers/python/media_worker.py"), "--root", str(ROOT)],
                                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8", env=environment)
        if json.loads(self.worker.stdout.readline())["kind"] != "hello":
            raise SystemExit("FAIL: the worker did not start.")

    def call(self, operation, payload):
        self.worker.stdin.write(json.dumps({"protocol": 1, "id": uuid.uuid4().hex, "operation": operation, "payload": payload}) + "\n")
        self.worker.stdin.flush()
        while True:
            event = json.loads(self.worker.stdout.readline())
            if event["kind"] == "result":
                return event["payload"]
            if event["kind"] == "error":
                raise SystemExit(f"FAIL: {operation}: {event['payload']['message']}")

    def close(self):
        self.worker.stdin.close()
        self.worker.wait(timeout=60)


def digest(image):
    return hashlib.sha256(Path(image["imagePath"]).read_bytes()).hexdigest()


def other_owner(data):
    return subprocess.run([sys.executable, "-I", "-c", OTHER_OWNER, str(ROOT / "workers/python"), data],
                          capture_output=True, text=True, timeout=60).stdout.strip()


def timing(image):
    return (f"{image['seconds']} s inference, load {image.get('loadSeconds')} s, runtime start {image.get('startupSeconds')} s"
            + (", kept loaded" if image.get("keptLoaded") else ""))


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    data = sys.argv[1]
    once = Coordinator(keep=False)
    try:
        started = time.monotonic()
        baseline = once.call("generate.image", dict(PICTURE, dataRoot=data))
        print(f"PASS: one-shot picture in {time.monotonic() - started:.1f} s ({timing(baseline)})", flush=True)
    finally:
        once.close()
    kept = Coordinator(keep=True)
    try:
        walls, pictures = [], []
        for attempt in range(3):
            started = time.monotonic()
            pictures.append(kept.call("generate.image", dict(PICTURE, dataRoot=data)))
            walls.append(time.monotonic() - started)
            print(f"   kept picture {attempt + 1}: {walls[-1]:.1f} s ({timing(pictures[-1])})", flush=True)
        if any(digest(picture) != digest(baseline) for picture in pictures):
            raise SystemExit("FAIL: a kept-loaded model made a different picture from the same settings")
        if pictures[0].get("keptLoaded") or not all(picture.get("keptLoaded") for picture in pictures[1:]):
            raise SystemExit("FAIL: repeats did not reuse the loaded model")
        print(f"PASS: three kept pictures are byte-identical to the one-shot picture; repeats took {walls[1]:.1f} and {walls[2]:.1f} s", flush=True)
        if other_owner(data) != "busy":
            raise SystemExit("FAIL: another program could take the data folder while the model was loaded")
        print("PASS: another program finds the data folder busy while the model is loaded", flush=True)
        changed = kept.call("generate.image", dict(PICTURE, dataRoot=data, sampler="euler-a"))
        restyled = kept.call("generate.image", dict(PICTURE, dataRoot=data, sourceImage=baseline["imagePath"], strength=0.6))
        again = kept.call("generate.image", dict(PICTURE, dataRoot=data))
        if digest(changed) == digest(baseline) or digest(again) != digest(baseline):
            raise SystemExit("FAIL: a sampler or image-to-image picture changed the next picture")
        print(f"PASS: after a sampler change ({timing(changed)}) and an image-to-image picture ({timing(restyled)}), "
              f"the original settings make the same picture again ({timing(again)})", flush=True)
    finally:
        kept.close()
    if other_owner(data) != "locked":
        raise SystemExit("FAIL: the data folder stayed busy after the coordinator ended")
    if (Path(data) / "temp").exists():
        raise SystemExit("FAIL: temporary folders were left in the data folder")
    print("PASS: ending the coordinator releases the model, the data folder and its temporary folders", flush=True)


if __name__ == "__main__":
    main()
