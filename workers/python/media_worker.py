"""Protocol 1 metadata worker. Standard library only; no model installation or generation."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import threading
import uuid

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hub_catalog
import inference_setup
import storage
import artifact_export

PROTOCOL = 1
MAX_REQUEST_BYTES = 1_048_576
OUTPUT_LOCK = threading.Lock()
ACTIVE_LOCK = threading.Lock()
active: tuple[str, threading.Event, threading.Thread] | None = None


def emit(request_id: str | None, kind: str, payload: dict) -> None:
    with OUTPUT_LOCK:
        print(json.dumps({"protocol": PROTOCOL, "id": request_id, "kind": kind,
                          "payload": payload}, ensure_ascii=False), flush=True)


def error(request_id: str | None, code: str, message: str) -> None:
    emit(request_id, "error", {"code": code, "message": message})


def finish(request_id, kind, payload):
    global active
    with ACTIVE_LOCK:
        if active and active[0] == request_id:
            active = None
    emit(request_id, kind, payload)


def gpu_probe() -> dict:
    executable = shutil.which("nvidia-smi")
    if not executable:
        return {"status": "unavailable", "reason": "nvidia-smi was not found"}
    try:
        run = subprocess.run([executable, "--query-gpu=name,memory.total,driver_version",
                              "--format=csv,noheader"], capture_output=True, text=True,
                             timeout=5, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        return {"status": "detected" if run.returncode == 0 else "probe_failed",
                "summary": run.stdout.strip()[:4096],
                "note": "Detection is not an inference compatibility test."}
    except (OSError, subprocess.TimeoutExpired):
        return {"status": "probe_failed", "reason": "Device probe failed or timed out"}


def execute(root: Path, request_id: str, operation: str, payload: dict,
            cancellation: threading.Event) -> None:
    global active
    try:
        if operation == "health":
            result = {"python": platform.python_version(), "executable": sys.executable,
                      "platform": platform.system(), "gpu": gpu_probe(),
                      "mode": "alpha", "generationAvailable": False,
                      "installationAvailable": True}
        elif operation == "catalog.list":
            recipes = [json.loads(path.read_text(encoding="utf-8"))
                       for path in sorted((root / "recipes").glob("*.json"))]
            result = {"source": "bundled recipes; readiness requires per-machine verification", "recipes": recipes}
        elif operation == "catalog.search":
            result = hub_catalog.search(payload)
        elif operation == "catalog.details":
            result = hub_catalog.details(payload)
        elif operation == "installation.status":
            result = inference_setup.status(root, payload)
        elif operation == "installation.run":
            result = inference_setup.install(root, payload, cancellation,
                lambda value: emit(request_id, "progress", value))
        elif operation == "generate.image":
            result = inference_setup.generate(root, payload, cancellation,
                lambda value: emit(request_id, "progress", value))
        elif operation == "upscale.image":
            result = inference_setup.upscale(root, payload, cancellation,
                lambda value: emit(request_id, "progress", value))
        elif operation == "library.list":
            result = inference_setup.library(payload, cancellation)
        elif operation == "library.export":
            result = artifact_export.export(root, payload, cancellation, lambda value: emit(request_id, "progress", value))
        elif operation == "storage.summary":
            result = storage.summary(payload)
        elif operation == "storage.clear":
            result = storage.clear(payload, cancellation, lambda value: emit(request_id, "progress", value))
        elif operation == "demo.run":
            steps = payload.get("steps", 5)
            delay_ms = payload.get("delayMs", 50)
            label = payload.get("label", "Protocol demonstration")
            if type(steps) is not int or not 1 <= steps <= 100:
                raise ValueError("steps must be an integer from 1 to 100")
            if type(delay_ms) is not int or not 0 <= delay_ms <= 1000:
                raise ValueError("delayMs must be an integer from 0 to 1000")
            if not isinstance(label, str) or len(label) > 4096:
                raise ValueError("label must be a string of at most 4096 characters")
            output = payload.get("outputDirectory")
            if not isinstance(output, str) or not output or not Path(output).is_absolute():
                raise ValueError("An explicit absolute outputDirectory is required")
            for step in range(steps):
                if cancellation.wait(delay_ms / 1000):
                    finish(request_id, "error", {"code": "cancelled", "message": "Protocol demonstration cancelled"})
                    return
                emit(request_id, "progress", {"stage": "protocol demonstration",
                     "completed": step + 1, "total": steps, "unit": "steps"})
            if cancellation.is_set():
                finish(request_id, "error", {"code": "cancelled", "message": "Protocol demonstration cancelled"})
                return
            directory = Path(output).resolve()
            directory.mkdir(parents=True, exist_ok=True)
            artifact = directory / f"demo-{uuid.uuid4().hex}.txt"
            with artifact.open("x", encoding="utf-8") as stream:
                stream.write("PROTOCOL DEMONSTRATION — NOT GENERATED MEDIA\n" + label + "\n")
            result = {"mode": "demonstration", "label": label,
                      "artifacts": [{"path": str(artifact), "mediaType": "text/plain"}]}
        else:
            finish(request_id, "error", {"code": "not_implemented", "message": f"Operation is not implemented: {operation}"})
            return
        finish(request_id, "result", result)
    except inference_setup.SetupError as exc:
        finish(request_id, "error", {"code": "cancelled" if isinstance(exc, inference_setup.Cancelled) else "inference_failed", "message": str(exc)})
    except hub_catalog.CatalogError as exc:
        finish(request_id, "error", {"code": "catalog_unavailable", "message": str(exc)})
    except (ValueError, TypeError) as exc:
        finish(request_id, "error", {"code": "invalid_input", "message": str(exc)})
    except Exception as exc:
        print(f"Worker operation failed ({type(exc).__name__})", file=sys.stderr, flush=True)
        finish(request_id, "error", {"code": "worker_error", "message": f"{type(exc).__name__}: {str(exc)[:1500]}"})
    finally:
        with ACTIVE_LOCK:
            if active and active[0] == request_id:
                active = None


def main() -> int:
    global active
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    root = parser.parse_args().root.resolve()
    if not (root / "recipes").is_dir():
        print("The selected root must contain recipes/", file=sys.stderr)
        return 2
    emit(None, "hello", {"worker": "media-workbench-python", "mode": "alpha",
                         "operations": ["health", "catalog.list", "catalog.search", "catalog.details", "demo.run",
                                        "installation.status", "installation.run", "generate.image", "upscale.image", "library.list", "library.export", "storage.summary", "storage.clear"],
                         "generationAvailable": False, "installationAvailable": True})
    while True:
        raw = sys.stdin.buffer.readline(MAX_REQUEST_BYTES + 1)
        if not raw:
            break
        if len(raw) > MAX_REQUEST_BYTES:
            error(None, "invalid_request", "Request exceeds 1 MiB; closing transport")
            break
        request_id = None
        try:
            request = json.loads(raw)
            if not isinstance(request, dict):
                raise ValueError("Request must be an object")
            request_id = request.get("id")
            if not isinstance(request_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", request_id):
                request_id = None
                raise ValueError("id must contain 1–128 letters, digits, hyphens or underscores")
            if type(request.get("protocol")) is not int or request["protocol"] != PROTOCOL:
                raise ValueError("Unsupported protocol")
            operation = request.get("operation")
            payload = request.get("payload")
            if not isinstance(operation, str) or not operation or len(operation) > 128:
                raise ValueError("Invalid operation")
            if not isinstance(payload, dict):
                raise ValueError("payload must be an object")
            with ACTIVE_LOCK:
                if operation == "cancel":
                    if active and active[0] == request_id:
                        active[1].set()
                    # Cancellation has no second terminal response; late cancellation is harmless.
                    continue
                if active:
                    error(request_id, "busy", "One operation at a time is supported")
                    continue
                cancellation = threading.Event()
                thread = threading.Thread(target=execute, args=(root, request_id, operation,
                                           payload, cancellation), daemon=True)
                active = (request_id, cancellation, thread)
                thread.start()
        except (ValueError, UnicodeError) as exc:
            error(request_id, "invalid_request", str(exc))
    with ACTIVE_LOCK:
        remaining = active
        if remaining:
            remaining[1].set()
    if remaining:
        remaining[2].join(timeout=6)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
