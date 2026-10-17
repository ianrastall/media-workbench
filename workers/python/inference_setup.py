"""App-owned SD 1.5 installation, durable attempts, and child-process supervision.

This module stays standard-library only and runs in the small discovery interpreter.
"""
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time
import urllib.request
import uuid
import zipfile

PROFILE = "sd15-cu126-v1"
RECIPE = "sd15-baseline"
SETUP_ACTIVE = ["preparing", "runtime", "downloading", "verifying"]


class SetupError(Exception):
    pass


class Cancelled(SetupError):
    pass


def check_cancel(cancel):
    if cancel.is_set():
        raise Cancelled("Operation cancelled. Downloaded files are retained for retry.")


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def data_root(payload):
    value = payload.get("dataRoot")
    if not isinstance(value, str) or not Path(value).is_absolute():
        raise ValueError("An absolute dataRoot is required")
    path = Path(value)
    if path == Path(path.anchor):
        raise ValueError("Choose a folder rather than a drive root")
    # Refuse existing links/junctions in the application-managed tree.
    for ancestor in [path, *path.parents]:
        if ancestor.is_symlink() or (hasattr(ancestor, "is_junction") and ancestor.is_junction()):
            raise ValueError("The data folder must not pass through a link or junction")
    path.mkdir(parents=True, exist_ok=True)
    return path.resolve()


def owned(root, relative):
    target = root / relative
    for part in [target, *target.parents]:
        if part == root.parent:
            break
        if part.is_symlink() or (hasattr(part, "is_junction") and part.is_junction()):
            raise SetupError("An application-managed path contains a link or junction")
    return target


@contextmanager
def exclusive(root):
    path = owned(root, ".operation.lock")
    with path.open("a+b") as stream:
        if os.fstat(stream.fileno()).st_size == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            raise SetupError("Another operation is using this data folder") from exc
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)


def profile_stamp(app):
    return hashlib.sha256(b"".join((app / "profiles" / p).read_bytes() for p in
        ["sd15-cu126.json", "sd15-cu126.lock", "sd15-files.json"]) +
        (app / "workers/python/inference_worker.py").read_bytes() +
        (app / "workers/python/inference_setup.py").read_bytes() +
        (app / "recipes/sd15-baseline.json").read_bytes()).hexdigest()


def state_path(root):
    return owned(root, "jobs/install-sd15.json")


def read_record(path):
    try:
        record = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None  # A damaged record is skipped rather than blocking every listing.
    return record if isinstance(record, dict) else None


def job_id_valid(value):
    return isinstance(value, str) and len(value) == 32 and all(c in "0123456789abcdef" for c in value)


def recover_interrupted(root):
    """Record work whose owner died. Call only while holding the data-folder lock: no other owner exists."""
    recovered = 0
    now = datetime.now(timezone.utc).isoformat()
    jobs = owned(root, "jobs")
    for stale in jobs.glob("*.json.*.tmp"):
        stale.unlink(missing_ok=True)  # An unfinished atomic write from a terminated owner.
    state = state_path(root)
    record = read_record(state) if state.exists() else None
    if record and record.get("status") in SETUP_ACTIVE:
        record.update(status="interrupted", message="Setup was interrupted. Install / verify to resume.", interruptedUtc=now)
        atomic_json(state, record)
        recovered += 1
    for path in jobs.glob("*.json"):
        if path.name == state.name:
            continue
        record = read_record(path)
        if not record or record.get("status") != "running":
            continue
        if job_id_valid(record.get("id")):
            owned(root, f"artifacts/{record['id']}/image.partial").unlink(missing_ok=True)
        record.update(status="interrupted", interruptedUtc=now,
                      message="Generation stopped before it finished; no image was added to Library.")
        atomic_json(path, record)
        recovered += 1
    return recovered


def recover_if_idle(root):
    try:
        with exclusive(root):
            return recover_interrupted(root)
    except SetupError:
        return 0  # A live owner holds the lock; its records are not stale.


def status(app, payload):
    root = data_root(payload)
    recover_if_idle(root)
    state = state_path(root)
    if not state.exists():
        return {"status": "not_installed", "message": "Stable Diffusion 1.5 is not installed."}
    record = json.loads(state.read_text(encoding="utf-8"))
    if record.get("status") == "ready" and record.get("profileStamp") != profile_stamp(app):
        record.update(status="needs_verification", message="The runtime recipe changed. Install / verify again.")
    if record.get("status") == "ready":
        required = owned(root, f"runtimes/{PROFILE}/python/python.exe")
        manifest = json.loads((app / "profiles/sd15-files.json").read_text())
        if not required.is_file() or any(not owned(root, f"models/{RECIPE}/" + e["path"]).is_file() for e in manifest["files"]):
            record.update(status="needs_verification", message="Runtime or model files are missing. Install / verify again.")
    return record


def download(entry, destination, cancel, progress):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists() and digest(destination) == entry["sha256"]:
        return
    partial = destination.with_suffix(destination.suffix + ".partial")
    progress({"stage": "Downloading " + destination.name})
    request = urllib.request.Request(entry["url"], headers={"User-Agent": "MediaWorkbench/0.2"})
    last = 0
    with urllib.request.urlopen(request, timeout=30) as response, partial.open("wb") as output:
        total = int(response.headers.get("Content-Length", 0))
        completed = 0
        while chunk := response.read(1024 * 1024):
            check_cancel(cancel)
            output.write(chunk)
            completed += len(chunk)
            if time.monotonic() - last > 0.5:
                progress({"stage": "Downloading " + destination.name, "completed": completed,
                          "total": total, "unit": "bytes"})
                last = time.monotonic()
    check_cancel(cancel)
    if digest(partial) != entry["sha256"]:
        raise SetupError("Download checksum mismatch: " + destination.name)
    os.replace(partial, destination)


def extract(archive, target):
    target.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as package:
        for member in package.infolist():
            relative = Path(member.filename)
            if relative.is_absolute() or ".." in relative.parts or ":" in member.filename:
                raise SetupError("Archive contains an invalid path")
            owned(target, relative)
        package.extractall(target)


def run_child(arguments, cancel, progress, environment=None, protocol=False, log_path=None):
    # No shell; the parent client owns this complete child tree on forced cancellation.
    child = subprocess.Popen([str(a) for a in arguments], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
        env=environment, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    lines = queue.Queue()
    def read():
        log = None
        try:
            if log_path:
                log_path.parent.mkdir(parents=True, exist_ok=True)
                log = log_path.open("w", encoding="utf-8")
            for line in child.stdout:
                if log:
                    log.write(line[:8192])
                    log.flush()
                lines.put(line[:8192])
        finally:
            if log:
                log.close()
            lines.put(None)
    threading.Thread(target=read, daemon=True).start()
    result = None
    tail = []
    try:
        while True:
            check_cancel(cancel)
            try:
                line = lines.get(timeout=0.1)
            except queue.Empty:
                continue
            if line is None:
                break
            tail = (tail + [line.strip()])[-8:]
            if protocol:
                try:
                    event = json.loads(line)
                except ValueError:
                    continue  # Third-party diagnostics are never sent as wire messages.
                if event.get("kind") == "progress":
                    progress(event["payload"])
                elif event.get("kind") == "result":
                    result = event["payload"]
                elif event.get("kind") == "error":
                    raise SetupError(event["payload"]["message"])
            elif line.strip():
                progress({"stage": line.strip()[:300]})
        code = child.wait(timeout=10)
        if code != 0 or (protocol and result is None):
            raise SetupError("Child process failed: " + "\n".join(tail)[-2000:])
        return result
    finally:
        if child.poll() is None:
            child.kill()
            child.wait(timeout=10)
        child.stdout.close()


def inference(app, root, operation, payload, cancel, progress):
    python = owned(root, f"runtimes/{PROFILE}/python/python.exe")
    environment = os.environ.copy()
    environment.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1",
        HF_HUB_DISABLE_IMPLICIT_TOKEN="1", HF_HOME=str(owned(root, "cache/huggingface")),
        PYTHONUTF8="1", PYTHONIOENCODING="utf-8", TOKENIZERS_PARALLELISM="false")
    return run_child([python, "-I", "-u", app / "workers/python/inference_worker.py", "--root", app,
        "--data-root", root, "--operation", operation, "--payload", json.dumps(payload)],
        cancel, progress, environment, protocol=True,
        log_path=owned(root, f"logs/{operation}-{uuid.uuid4().hex}.log"))


def verify_files(app, root, cancel, progress):
    manifest = json.loads((app / "profiles/sd15-files.json").read_text())
    model = owned(root, f"models/{RECIPE}")
    for i, entry in enumerate(manifest["files"]):
        check_cancel(cancel)
        path = owned(model, entry["path"])
        progress({"stage": "Verifying " + entry["path"], "completed": i,
                  "total": len(manifest["files"]), "unit": "files"})
        if not path.is_file() or path.stat().st_size != entry["size"] or digest(path) != entry["sha256"]:
            raise SetupError("Model verification failed: " + entry["path"] + ". Install / verify again.")


def install(app, payload, cancel, progress):
    root = data_root(payload)
    with exclusive(root):
        recover_interrupted(root)
        record = {"schemaVersion": 1, "recipeId": RECIPE, "attemptId": uuid.uuid4().hex,
                  "status": "preparing", "startedUtc": datetime.now(timezone.utc).isoformat(),
                  "recipe": json.loads((app / "recipes/sd15-baseline.json").read_text(encoding="utf-8")),
                  "runtimeProfileId": PROFILE}
        def stage(name, message):
            record.update(status=name, message=message)
            atomic_json(state_path(root), record)
            progress({"stage": message})
        try:
            stage("preparing", "Checking storage and NVIDIA GPU")
            if shutil.disk_usage(root).free < 20 * 1024**3:
                raise SetupError("At least 20 GiB of free space is required for setup and its download cache.")
            if os.name != "nt" or not shutil.which("nvidia-smi"):
                raise SetupError("This first inference profile requires Windows x64 and an NVIDIA CUDA GPU.")
            gpu = subprocess.run([shutil.which("nvidia-smi"), "--query-gpu=memory.total", "--format=csv,noheader,nounits"],
                capture_output=True, text=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
            if gpu.returncode != 0 or not any(float(line.strip()) >= 5632 for line in gpu.stdout.splitlines() if line.strip()):
                raise SetupError("Setup requires an NVIDIA GPU with at least 6 GB VRAM and a working driver.")
            profile = json.loads((app / "profiles/sd15-cu126.json").read_text())
            bootstrap = owned(root, "bootstrap")
            runtime = owned(root, f"runtimes/{PROFILE}/python")
            stage("runtime", "Preparing private Python 3.12 and locked CUDA packages")
            for key in ["python", "uv"]:
                download(profile[key], owned(bootstrap, key + ".zip"), cancel, progress)
            extract(bootstrap / "python.zip", runtime)
            (runtime / "python312._pth").write_text("python312.zip\n.\nLib/site-packages\nimport site\n")
            uvroot = owned(bootstrap, "uv-" + profile["uv"]["version"])
            if not (uvroot / "uv.exe").exists():
                extract(bootstrap / "uv.zip", uvroot)
            environment = {k: v for k, v in os.environ.items() if not k.startswith(("UV_", "PIP_", "PYTHON"))}
            environment.update(UV_PYTHON_DOWNLOADS="never", UV_NO_CONFIG="1", PYTHONUTF8="1")
            run_child([uvroot / "uv.exe", "pip", "sync", app / "profiles/sd15-cu126.lock",
                "--python", runtime / "python.exe", "--target", owned(runtime, "Lib/site-packages"),
                "--require-hashes", "--only-binary", ":all:", "--no-config", "--no-progress",
                "--cache-dir", owned(root, "cache/uv"), "--link-mode", "copy"], cancel, progress, environment)
            stage("downloading", "Fetching pinned Hugging Face model files")
            inference(app, root, "download", {}, cancel, progress)
            stage("verifying", "Verifying all model checksums")
            verify_files(app, root, cancel, progress)
            stage("verifying", "Testing 512 × 512 inference on your GPU")
            evidence = inference(app, root, "verify", {}, cancel, progress)
            check_cancel(cancel)
            record.update(status="ready", message="Stable Diffusion 1.5 is ready on " + evidence["gpu"],
                          profileStamp=profile_stamp(app), verifiedUtc=datetime.now(timezone.utc).isoformat(),
                          evidence=evidence)
            atomic_json(state_path(root), record)
            return record
        except Exception as exc:
            record.update(status="cancelled" if isinstance(exc, Cancelled) else "failed", message=str(exc))
            atomic_json(state_path(root), record)
            raise


def validate_generation(payload):
    prompt = payload.get("prompt")
    negative = payload.get("negativePrompt", "")
    seed = payload.get("seed", 0)
    steps = payload.get("steps", 20)
    if not isinstance(prompt, str) or not 1 <= len(prompt.strip()) <= 2000:
        raise ValueError("Enter a prompt of 1–2000 characters")
    if not isinstance(negative, str) or len(negative) > 2000:
        raise ValueError("Negative prompt is too long")
    if type(seed) is not int or not 0 <= seed <= 4294967295:
        raise ValueError("Seed must be an integer from 0 to 4294967295")
    if type(steps) is not int or not 1 <= steps <= 50:
        raise ValueError("Steps must be an integer from 1 to 50")
    return {"prompt": prompt.strip(), "negativePrompt": negative, "seed": seed, "steps": steps,
            "width": 512, "height": 512, "guidanceScale": 7.5}


def generate(app, payload, cancel, progress):
    settings = validate_generation(payload)
    root = data_root(payload)
    with exclusive(root):
        recover_interrupted(root)
        ready = json.loads(state_path(root).read_text()) if state_path(root).exists() else {}
        if ready.get("status") != "ready" or ready.get("profileStamp") != profile_stamp(app):
            raise SetupError("Install / verify Stable Diffusion 1.5 before generating.")
        verify_files(app, root, cancel, progress)
        job_id = uuid.uuid4().hex
        manifest = json.loads((app / "profiles/sd15-files.json").read_text())
        record = dict(schemaVersion=1, id=job_id, status="running", settings=settings,
                      startedUtc=datetime.now(timezone.utc).isoformat(), recipeId=RECIPE,
                      profileStamp=ready["profileStamp"], runtimeProfileId=PROFILE,
                      source=dict(provider="huggingface", repoId=manifest["repoId"], revision=manifest["revision"]))
        job = owned(root, f"jobs/{job_id}.json")
        atomic_json(job, record)
        try:
            result = inference(app, root, "generate", dict(settings, jobId=job_id), cancel, progress)
            check_cancel(cancel)
            record.update(status="succeeded", completedUtc=datetime.now(timezone.utc).isoformat(), **result)
            atomic_json(job, record)
            return record
        except Exception as exc:
            record.update(status="cancelled" if isinstance(exc, Cancelled) else "failed", message=str(exc))
            atomic_json(job, record)
            raise


def library(payload):
    root = data_root(payload)
    recovered = recover_if_idle(root)
    jobs = owned(root, "jobs")
    results = []
    for path in sorted(jobs.glob("*.json"), key=lambda p: p.stat().st_mtime, reverse=True)[:200]:
        record = read_record(path)
        if record and record.get("status") == "succeeded" and isinstance(record.get("imagePath"), str):
            image = Path(record["imagePath"])
            if image.is_relative_to(root / "artifacts") and image.is_file():
                results.append(record)
    return {"artifacts": results, "recovered": recovered}
