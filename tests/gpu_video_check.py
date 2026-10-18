"""Opt-in real GPU check for short videos: setup, a real clip, MP4 encoding, Library, and export.

Usage (from the repository, after scripts/build.ps1):
    runtime/python/python.exe tests/gpu_video_check.py <data folder> [--expect <video runtime>] [--frames 16|24]

Installs / verifies Stable Diffusion 1.5 first when the folder is not ready (about 5 GiB of downloads and a GPU
test), then runs Set up videos: it downloads the pinned AnimateDiff motion adapter (1.7 GB), verifies it, and makes
a real 16-frame test clip on the GPU, encoded with Windows' H.264 encoder. It then makes a 20-step video, checks the
MP4 and poster in Library, and saves a verified copy beside the data folder. Not part of check.ps1.
"""
import json
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    data = sys.argv[1]
    expected = sys.argv[sys.argv.index("--expect") + 1] if "--expect" in sys.argv else None
    frames = int(sys.argv[sys.argv.index("--frames") + 1]) if "--frames" in sys.argv else 16
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
        if call("installation.status", {"dataRoot": data})["status"] != "ready":
            installed = call("installation.run", {"dataRoot": data, "keepDownloadCaches": False})
            print(f"PASS: Stable Diffusion 1.5 {installed['status']}: {installed['message']}")
        ready = call("video.install", {"dataRoot": data})
        record = json.loads((Path(data) / "jobs/install-video.json").read_text(encoding="utf-8"))
        evidence = record["evidence"]
        print(f"PASS: videos {ready['status']} with {record['runtime']} ({evidence['precision']}, {evidence['offload']} offload): "
              f"16-frame 4-step test {evidence['seconds']} s after loading {evidence.get('loadSeconds')} s, peak {evidence['peakVramMiB']} MiB. {ready['message']}")
        for item in record.get("declinedRuntimes") or []:
            print(f"   declined {item['runtime']} on {item['gpu']} (driver {item['driver']}): {item['message'][:200]}")
        if expected and record["runtime"] != expected:
            raise SystemExit(f"FAIL: expected {expected}, videos were verified with {record['runtime']}")
        clip = call("generate.video", dict(dataRoot=data, prompt="Ocean waves rolling onto a sandy beach at sunset, cinematic",
                                           negativePrompt="blurry, low quality", seed=20261018, steps=20, frames=frames))
        video, poster = Path(clip["videoPath"]), Path(clip["imagePath"])
        if not video.is_file() or video.read_bytes()[4:8] != b"ftyp" or not poster.is_file():
            raise SystemExit("FAIL: the video or its poster was not saved")
        print(f"PASS: {clip['frames']}-frame video {clip['width']}x{clip['height']} in {clip['seconds']} s after loading {clip.get('loadSeconds')} s, "
              f"peak {clip['peakVramMiB']} MiB, {video.stat().st_size / 1024:.0f} KiB MP4 on {clip['gpu']}")
        if clip.get("loadNote"):
            print("   " + clip["loadNote"])
        listed = call("library.list", {"dataRoot": data, "kind": "video", "query": clip["id"]})
        if [item["id"] for item in listed["artifacts"]] != [clip["id"]]:
            raise SystemExit("FAIL: Library did not list the video")
        copy = Path(data).parent / f"media-workbench-video-check-{clip['id']}.mp4"
        saved = call("library.export", {"dataRoot": data, "jobId": clip["id"], "destination": str(copy)})
        if saved["sha256"] != clip["videoSha256"] or copy.read_bytes() != video.read_bytes():
            raise SystemExit("FAIL: the saved copy differs from the video")
        copy.unlink()
        print("PASS: Library lists the video and Save a copy wrote a verified MP4")
    finally:
        worker.stdin.close()
        worker.wait(timeout=30)


if __name__ == "__main__":
    main()
