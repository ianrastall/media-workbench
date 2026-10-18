"""Opt-in real GPU check: the hardware profile chooses settings, and those settings pass on this GPU.

Usage (from the repository, after scripts/build.ps1):
    runtime/python/python.exe tests/gpu_hardware_profile_check.py <data folder> [--expect <runtime>]

Reads this computer's profile, runs Install / verify (which tests the chosen runtime on the GPU and
falls back to the conservative one if it fails), makes a 20-step 512 x 512 picture with the verified
runtime, and enlarges it 4x with the tile size chosen from free graphics memory. Install / verify
re-tests the data folder's installation and may download missing files; it is not part of check.ps1.
--expect fails the check when the recommended runtime differs (for example fp32-offload on a GTX 16-series card).
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
        profile = call("system.profile", {"dataRoot": data})
        found, recommended = profile["hardware"], profile["recommended"]
        gpu = found["gpu"] or {}
        print(f"PASS: profile: {gpu.get('name')} {gpu.get('vramMiB')} MiB (compute {gpu.get('computeCapability') or '?'}, driver {gpu.get('driver')}), "
              f"RAM {found['ramMiB']} MiB, data drive {found['drive']['kind']} with {found['drive']['freeBytes'] / 1024**3:.0f} GiB free")
        if recommended["blocked"]:
            raise SystemExit("FAIL: this computer cannot make pictures: " + recommended["blocked"])
        chosen = recommended["runtime"]
        print(f"PASS: recommended {chosen}: " + " ".join(recommended["reasons"] + recommended["warnings"]))
        if expected and chosen != expected:
            raise SystemExit(f"FAIL: expected {expected}, the profile chose {chosen}")
        # Keep the folder's download caches: this check is about the runtime choice, not clean-up.
        installed = call("installation.run", {"dataRoot": data, "keepDownloadCaches": True})
        record = json.loads((Path(data) / "jobs/install-sd15.json").read_text(encoding="utf-8"))
        evidence = record["evidence"]
        if installed["status"] != "ready" or record["runtime"] != evidence["runtime"]:
            raise SystemExit(f"FAIL: install / verify ended {installed['status']}: {installed.get('message')}")
        print(f"PASS: verified {record['runtime']} ({evidence['precision']}, offload {evidence['offload']}): 4-step test "
              f"{evidence['seconds']} s, peak {evidence['peakVramMiB']} MiB. {installed['message']}")
        for item in record.get("declinedRuntimes") or []:
            print(f"   declined {item['runtime']} on {item['gpu']} (driver {item['driver']}): {item['message'][:200]}")
        if record["runtime"] != chosen and not any(item["runtime"] == chosen for item in record.get("declinedRuntimes") or []):
            raise SystemExit("FAIL: the verified runtime is neither the chosen one nor a recorded fallback")
        status = call("installation.status", {"dataRoot": data})
        if status["status"] != "ready":
            raise SystemExit(f"FAIL: status after verification is {status['status']}")
        picture = call("generate.image", dict(dataRoot=data, prompt="A lighthouse on a rocky coast at sunset, oil painting",
                                              seed=20261017, steps=20))
        if picture["runtime"] != record["runtime"] or picture["precision"] != evidence["precision"]:
            raise SystemExit("FAIL: generation did not use the verified runtime")
        print(f"PASS: text to image with {picture['runtime']}: {picture['width']}x{picture['height']}, 20 steps, "
              f"{picture['seconds']} s, peak {picture['peakVramMiB']} MiB")
        bigger = call("upscale.image", {"dataRoot": data, "sourceImage": picture["imagePath"], "scale": 4})
        print(f"PASS: 4x upscale to {bigger['width']}x{bigger['height']} with {bigger['tile']}-pixel tiles in {bigger['seconds']} s, "
              f"peak {bigger['peakVramMiB']} MiB")
        after = call("system.profile", {"dataRoot": data})
        if (after["verified"] or {}).get("id") != record["runtime"]:
            raise SystemExit("FAIL: the profile does not report the verified runtime")
        print("PASS: the profile reports the verified runtime" + (f"; note: {after['note']}" if after["note"] else ""))
        for image in (picture, bigger):
            print("   ", image["imagePath"])
    finally:
        worker.stdin.close()
        worker.wait(timeout=30)


if __name__ == "__main__":
    main()
