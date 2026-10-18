"""App-owned SD 1.5 installation, durable attempts, and child-process supervision.

This module stays standard-library only and runs in the small discovery interpreter.
"""
from contextlib import contextmanager
import ctypes
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
UPSCALER = "realesrgan-x4plus"
SETUP_ACTIVE = ["preparing", "runtime", "downloading", "verifying"]
# Output sizes and samplers offered for SD 1.5; verification itself always runs the 512 x 512 default.
SIZES = [(512, 512), (512, 768), (768, 512)]
SAMPLERS = ["pndm", "dpmpp-2m", "euler-a"]
SOURCE_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp", ".bmp"}
SOURCE_MAX_BYTES = 50 * 1024**2
LIBRARY_IMAGES = 200
LIBRARY_PROBLEMS = 50
PROBLEM_STATUSES = {"failed", "cancelled", "interrupted"}
# Bump when this module or inference_worker.py changes what a verified installation means
# (pipeline loading, precision, checks). Source edits alone no longer revoke readiness.
ADAPTER_VERSION = 2  # 2: verification also tests the Real-ESRGAN upscaler.
STAMPED_FILES = ["profiles/sd15-cu126.json", "profiles/sd15-cu126.lock", "profiles/sd15-files.json",
                 "profiles/upscaler-files.json", "recipes/sd15-baseline.json"]
UPSCALE_MAX_SIDE = 2048
# Painted changes keep the picture's own size; larger pictures would make very large PNGs.
EDIT_MAX_SIDE = 4096
FRESH_SETUP_BYTES = 20 * 1024**3  # Runtime, wheel cache, model, and download temporaries.
SETUP_HEADROOM_BYTES = 2 * 1024**3
# Drivers report slightly less than nominal capacity (a 6 GB GTX 1660 Ti reports 6143.7 MiB to CUDA).
VRAM_REPORTING_MARGIN_MIB = 128


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
        # Windows permits byte-range locks beyond EOF; flock needs no file content either.
        # No sentinel write/flush before acquiring the lock: concurrent first owners must
        # contend on the lock itself, without racing a write or waiting for disk writeback.
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
    """Identity of a verified installation: adapter version plus the pinned profile, lock, manifest, and recipe."""
    lines = [f"adapter={ADAPTER_VERSION}"]
    for name in STAMPED_FILES:
        content = (app / name).read_bytes().replace(b"\r\n", b"\n")  # Checkout line endings are not a recipe change.
        lines.append(f"{name}={hashlib.sha256(content).hexdigest()}")
    return hashlib.sha256("\n".join(lines).encode()).hexdigest()


def native_core_path(app):
    """The Rust core beside the published app, or the developer build output in a source checkout."""
    for candidate in [app / "media_workbench_core.dll", app / "artifacts/native/release/media_workbench_core.dll"]:
        if candidate.is_file():
            return candidate
    return None


def plan_installation(app):
    """Run the recipe through the Rust installation gate (C ABI 1), so every client of the worker passes it."""
    path = native_core_path(app)
    if path is None:
        raise SetupError("The native installation planner is missing. Reinstall Media Workbench.")
    recipe = (app / "recipes/sd15-baseline.json").read_bytes()
    try:
        core = ctypes.CDLL(str(path))
    except OSError as exc:
        raise SetupError(f"The native installation planner could not be loaded: {exc}") from None
    core.mw_abi_version.restype = ctypes.c_uint32
    if core.mw_abi_version() != 1:
        raise SetupError("The native installation planner has an incompatible ABI.")
    plan = core.mw_plan_installation
    plan.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    plan.restype = ctypes.c_int32
    required = ctypes.c_size_t()
    code = plan(recipe, len(recipe), None, 0, ctypes.byref(required))
    if code != 2 or required.value > 1024 * 1024:
        raise SetupError(f"Native installation planning failed ({code}).")
    output = ctypes.create_string_buffer(required.value)
    code = plan(recipe, len(recipe), output, required.value, ctypes.byref(required))
    if code != 0:
        raise SetupError(f"Native installation planning failed ({code}).")
    result = json.loads(output.raw[:required.value])
    if not result.get("canExecute"):
        raise SetupError("This recipe may not be installed: " + "; ".join(result.get("blockers") or ["blocked"]))
    return result


def lock_digest(app):
    return hashlib.sha256((app / "profiles/sd15-cu126.lock").read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def runtime_marker(root):
    return owned(root, f"runtimes/{PROFILE}/.synced-lock-sha256")


def model_files(app, root):
    """(stamp key, path, manifest entry) for every verified model file: SD 1.5, then the upscaler."""
    manifest = json.loads((app / "profiles/sd15-files.json").read_text())
    model = owned(root, f"models/{RECIPE}")
    files = [(entry["path"], owned(model, entry["path"]), entry) for entry in manifest["files"]]
    upscaler = json.loads((app / "profiles/upscaler-files.json").read_text())
    folder = owned(root, f"models/{UPSCALER}")
    return files + [(f"{UPSCALER}/{entry['name']}", owned(folder, entry["name"]), entry) for entry in upscaler["files"]]


def required_free_bytes(app, root):
    """A fresh setup needs room for everything; re-verifying a synced runtime needs only the missing model bytes."""
    marker = runtime_marker(root)
    python = owned(root, f"runtimes/{PROFILE}/python/python.exe")
    if not python.is_file() or not marker.is_file() or marker.read_text(encoding="utf-8").strip() != lock_digest(app):
        return FRESH_SETUP_BYTES
    missing = 0
    for _, path, entry in model_files(app, root):
        if not path.is_file() or path.stat().st_size != entry["size"]:
            missing += entry["size"]
    return missing + SETUP_HEADROOM_BYTES


def minimum_vram_mib(app):
    value = json.loads((app / "recipes/sd15-baseline.json").read_text(encoding="utf-8"))["minimumVramMiB"]
    if type(value) is not int or value <= 0:
        raise SetupError("The recipe does not declare a valid minimumVramMiB.")
    return value


def meets_vram(total_mib, minimum_mib):
    """Shared by setup (nvidia-smi) and the ML child (CUDA) so both apply the same limit."""
    return total_mib + VRAM_REPORTING_MARGIN_MIB >= minimum_mib


def select_gpu(listing, visible=None):
    """Pick the nvidia-smi row for the device the ML child sees as cuda:0.

    `listing` is `nvidia-smi --query-gpu=index,uuid,name,memory.total --format=csv,noheader,nounits`
    output. The child runs with CUDA_DEVICE_ORDER=PCI_BUS_ID, matching nvidia-smi ordering, so its
    device 0 is the first CUDA_VISIBLE_DEVICES entry, or index 0 when that is unset.
    Returns (name, total MiB).
    """
    rows = []
    for line in listing.splitlines():
        if line.strip():
            parts = [part.strip() for part in line.split(",")]
            if len(parts) != 4:
                raise SetupError("nvidia-smi returned an unrecognized GPU listing: " + line.strip()[:200])
            rows.append(parts)
    if not rows:
        raise SetupError("nvidia-smi did not list any NVIDIA GPU. Check that the NVIDIA driver is installed.")
    wanted = (visible or "").split(",")[0].strip() if visible is not None else "0"
    if not wanted:
        raise SetupError("CUDA_VISIBLE_DEVICES hides every GPU; clear it or select an NVIDIA GPU.")
    match = next((row for row in rows if row[0] == wanted or row[1].startswith(wanted)), None)
    if match is None:
        raise SetupError(f"CUDA_VISIBLE_DEVICES selects GPU '{wanted[:80]}', which nvidia-smi did not list.")
    index, _, name, memory = match
    try:
        total = float(memory)
    except ValueError:
        raise SetupError(f"The NVIDIA driver did not report memory for GPU {index} ({name}): it returned "
                         f"'{memory[:40]}'. Update the driver, or select another GPU with CUDA_VISIBLE_DEVICES.") from None
    return name, total


def check_gpu(app):
    minimum = minimum_vram_mib(app)
    try:
        gpu = subprocess.run([shutil.which("nvidia-smi"), "--query-gpu=index,uuid,name,memory.total",
            "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=10,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise SetupError("nvidia-smi could not be run; check the NVIDIA driver: " + str(exc)) from None
    if gpu.returncode != 0:
        raise SetupError("nvidia-smi failed; check the NVIDIA driver. " + (gpu.stderr or gpu.stdout).strip()[:300])
    name, total = select_gpu(gpu.stdout, os.environ.get("CUDA_VISIBLE_DEVICES"))
    if not meets_vram(total, minimum):
        raise SetupError(f"{name} reports {total:.0f} MiB of GPU memory; this recipe requires {minimum} MiB.")
    return name


def file_stamp(path):
    info = path.stat()
    return {"size": info.st_size, "mtimeNs": info.st_mtime_ns}


def files_unchanged(app, root, record):
    """Cheap per-run check against the size/mtime recorded once full SHA-256 verification passed."""
    stamps = record.get("files")
    if not isinstance(stamps, dict):
        return False
    for key, path, _ in model_files(app, root):
        try:
            if file_stamp(path) != stamps.get(key):
                return False
        except OSError:
            return False
    return True


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


def recover_interrupted(root, job_paths=None):
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
    for path in jobs.glob("*.json") if job_paths is None else job_paths:
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
    from model_library import recover as recover_imports  # Local import: model_library imports this module.
    recover_imports(root)  # First imports never passed their test; they are removed, not listed as jobs.
    return recovered


def recover_if_idle(root, job_paths=None):
    try:
        with exclusive(root):
            return recover_interrupted(root, job_paths)
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
        if not required.is_file() or not files_unchanged(app, root, record):
            record.update(status="needs_verification", message="Runtime or model files are missing or changed. Install / verify again.")
    record.pop("files", None)  # Internal stamps; callers need status and message.
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


def inference(app, root, operation, payload, cancel, progress, log_id=None):
    python = owned(root, f"runtimes/{PROFILE}/python/python.exe")
    environment = os.environ.copy()
    environment.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1",
        HF_HUB_DISABLE_IMPLICIT_TOKEN="1", HF_HOME=str(owned(root, "cache/huggingface")),
        PYTHONUTF8="1", PYTHONIOENCODING="utf-8", TOKENIZERS_PARALLELISM="false",
        CUDA_DEVICE_ORDER="PCI_BUS_ID")  # cuda:0 is then the device check_gpu inspected.
    return run_child([python, "-I", "-u", app / "workers/python/inference_worker.py", "--root", app,
        "--data-root", root, "--operation", operation, "--payload", json.dumps(payload)],
        cancel, progress, environment, protocol=True,
        log_path=owned(root, f"logs/{operation}-{log_id or uuid.uuid4().hex}.log"))


def verify_files(app, root, cancel, progress):
    """Full SHA-256 verification; returns the size/mtime stamps that later runs compare against."""
    from file_verification import verify
    files = model_files(app, root)
    stamps = {}
    for key, path, entry in files:
        check_cancel(cancel)
        before = file_stamp(path) if path.is_file() else None
        verify(app, root, path, entry, cancel, progress, key)
        if not before or file_stamp(path) != before:
            raise SetupError("Model verification failed: " + key + ". Install / verify again.")
        stamps[key] = before
    return stamps


def install(app, payload, cancel, progress):
    root = data_root(payload)
    plan_installation(app)  # Before any record: a blocked recipe never starts an attempt.
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
            needed = required_free_bytes(app, root)
            if shutil.disk_usage(root).free < needed:
                raise SetupError(f"At least {needed / 1024**3:.1f} GiB of free space is required in the data folder for this setup step.")
            if os.name != "nt" or not shutil.which("nvidia-smi"):
                raise SetupError("This first inference profile requires Windows x64 and an NVIDIA CUDA GPU.")
            check_gpu(app)
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
            runtime_marker(root).write_text(lock_digest(app), encoding="utf-8")
            stage("downloading", "Fetching pinned Hugging Face model files")
            inference(app, root, "download", {}, cancel, progress)
            stage("downloading", "Fetching the picture upscaler")
            upscaler = json.loads((app / "profiles/upscaler-files.json").read_text())
            for entry in upscaler["files"]:
                download(entry, owned(root, f"models/{UPSCALER}/{entry['name']}"), cancel, progress)
            stage("verifying", "Verifying all model checksums")
            stamps = verify_files(app, root, cancel, progress)
            stage("verifying", "Testing 512 × 512 inference and upscaling on your GPU")
            evidence = inference(app, root, "verify", {}, cancel, progress)
            check_cancel(cancel)
            record.update(status="ready", message="Stable Diffusion 1.5 is ready on " + evidence["gpu"],
                          profileStamp=profile_stamp(app), verifiedUtc=datetime.now(timezone.utc).isoformat(),
                          evidence=evidence, files=stamps)
            atomic_json(state_path(root), record)
            return {k: v for k, v in record.items() if k != "files"}
        except Exception as exc:
            record.update(status="cancelled" if isinstance(exc, Cancelled) else "failed", message=str(exc))
            atomic_json(state_path(root), record)
            raise


def validate_generation(payload):
    prompt = payload.get("prompt")
    negative = payload.get("negativePrompt", "")
    seed = payload.get("seed", 0)
    steps = payload.get("steps", 20)
    guidance = payload.get("guidanceScale", 7.5)
    size = (payload.get("width", 512), payload.get("height", 512))
    sampler = payload.get("sampler", "pndm")
    source = payload.get("sourceImage") or None
    strength = payload.get("strength", 0.6)
    mask = payload.get("maskImage") or None
    model = payload.get("model") or RECIPE
    if not isinstance(prompt, str) or not 1 <= len(prompt.strip()) <= 2000:
        raise ValueError("Enter a prompt of 1–2000 characters")
    if not isinstance(negative, str) or len(negative) > 2000:
        raise ValueError("Negative prompt is too long")
    if type(seed) is not int or not 0 <= seed <= 4294967295:
        raise ValueError("Seed must be an integer from 0 to 4294967295")
    if type(steps) is not int or not 1 <= steps <= 50:
        raise ValueError("Steps must be an integer from 1 to 50")
    if type(guidance) not in (int, float) or not 1 <= guidance <= 20:
        raise ValueError("Guidance must be a number from 1 to 20")
    if not all(type(side) is int for side in size) or size not in SIZES:
        raise ValueError("Size must be 512 × 512, 512 × 768, or 768 × 512")
    if sampler not in SAMPLERS:
        raise ValueError("Sampler must be one of: " + ", ".join(SAMPLERS))
    if model != RECIPE and not job_id_valid(model):
        raise ValueError("Choose one of the listed models")
    settings = {"prompt": prompt.strip(), "negativePrompt": negative, "seed": seed, "steps": steps,
                "width": size[0], "height": size[1], "guidanceScale": round(float(guidance), 2), "sampler": sampler,
                "model": model}
    if source is not None:
        path = validate_picture(source, "starting picture")
        if type(strength) not in (int, float) or not 0.05 <= strength <= 1:
            raise ValueError("How much to change must be from 0.05 to 1")
        settings.update(sourceImage=str(path), strength=round(float(strength), 2))
    if mask is not None:
        if source is None:
            raise ValueError("Choose a starting picture before painting the part to change")
        path = validate_picture(mask, "painted area")
        if path.suffix.lower() != ".png":
            raise ValueError("The painted area must be a PNG file")
        settings["maskImage"] = str(path)
    return settings


def validate_picture(source, role="picture"):
    if not isinstance(source, str) or not Path(source).is_absolute():
        raise ValueError(f"Choose the {role} by its full path")
    path = Path(source)
    if path.suffix.lower() not in SOURCE_SUFFIXES:
        raise ValueError(f"The {role} must be a PNG, JPEG, WebP, or BMP file")
    if not path.is_file():
        raise ValueError(f"The {role} could not be found")
    if path.stat().st_size > SOURCE_MAX_BYTES:
        raise ValueError(f"The {role} is larger than 50 MB")
    return path


def validate_upscale(payload):
    scale = payload.get("scale", 4)
    if type(scale) is not int or scale not in (2, 4):
        raise ValueError("Upscale by 2 or 4 times")
    return {"sourceImage": str(validate_picture(payload.get("sourceImage"))), "scale": scale}


def shape_for(width, height):
    """The supported shape closest to a picture's proportions."""
    ratio = width / height
    return (768, 512) if ratio >= 1.25 else (512, 768) if ratio <= 0.8 else (512, 512)


def mask_matches(mask_size, picture_size, maximum=512):
    """Whether a painted-area grid has the cell size the app derives from this picture (within rounding)."""
    scale = min(1, maximum / max(picture_size))
    return all(abs(cells - max(1, round(side * scale))) <= 1 for cells, side in zip(mask_size, picture_size))


def edit_region(bounds, size, minimum=384):
    """The box around painted pixels that the model redraws, and the supported shape it is drawn at.

    `bounds` is (left, top, right, bottom) of the painted pixels in a picture of `size`. The box adds
    surrounding context, takes the nearest supported proportions, and stays inside the picture.
    """
    width, height = size
    left, top, right, bottom = bounds
    pad = max(32, round(0.25 * max(right - left, bottom - top)))
    w, h = right - left + 2 * pad, bottom - top + 2 * pad
    shape = shape_for(w, h)
    ratio = shape[0] / shape[1]
    w, h = max(w, h * ratio), max(h, w / ratio)
    grow = max(1, min(minimum, max(width, height)) / max(w, h))
    w, h = min(width, w * grow), min(height, h * grow)
    cx, cy = (left + right) / 2, (top + bottom) / 2
    x0 = min(max(0, cx - w / 2), width - w)
    y0 = min(max(0, cy - h / 2), height - h)
    box = (round(x0), round(y0), round(x0 + w), round(y0 + h))
    return box, shape_for(box[2] - box[0], box[3] - box[1])


def resolve_settings(root, record):
    """Settings for clients: a stored starting picture or painted area becomes its absolute path, or is dropped if gone."""
    settings = dict(record.get("settings") or {})
    for key in ("sourceImage", "maskImage"):
        stored = settings.get(key)
        if stored is None:
            continue
        relative = Path(stored) if isinstance(stored, str) else None
        path = (owned(root, "artifacts") / relative).resolve() if relative and not relative.is_absolute() and job_id_valid(record.get("id")) else None
        if path and relative.parts[:1] == (record["id"],) and path.is_relative_to(owned(root, "artifacts").resolve() / record["id"]) and path.is_file():
            settings[key] = str(path)
        else:
            settings.pop(key, None)
    if "sourceImage" not in settings:
        settings.pop("maskImage", None)  # A painted area means nothing without its picture.
    return settings


def generate(app, payload, cancel, progress):
    settings = validate_generation(payload)  # Before data_root, which creates the folder.
    return run_job(app, data_root(payload), "generate", settings, cancel, progress)


def upscale(app, payload, cancel, progress):
    settings = validate_upscale(payload)
    return run_job(app, data_root(payload), "upscale", settings, cancel, progress)


def run_job(app, root, kind, settings, cancel, progress):
    """One generation or upscale: readiness gate, durable record, private copy of any input picture, owned child."""
    with exclusive(root):
        recover_interrupted(root)
        ready = json.loads(state_path(root).read_text()) if state_path(root).exists() else {}
        if ready.get("status") != "ready" or ready.get("profileStamp") != profile_stamp(app):
            raise SetupError("Install / verify Stable Diffusion 1.5 before generating.")
        if not files_unchanged(app, root, ready):
            raise SetupError("Model files changed since they were verified. Install / verify again.")
        job_id = uuid.uuid4().hex
        manifest = json.loads((app / "profiles/sd15-files.json").read_text())
        source = dict(provider="huggingface", repoId=manifest["repoId"], revision=manifest["revision"])
        model, model_name, extra = RECIPE, "Stable Diffusion 1.5", {}
        if settings.get("model", RECIPE) != RECIPE:
            import model_library
            added = model_library.ready_model(app, root, settings["model"])
            model, model_name, extra = added["id"], added["name"], model_library.child_payload(added)
            source = dict(provider="local", name=added.get("sourceName", ""), sha256=added["files"][0]["sha256"])
        record = dict(schemaVersion=1, id=job_id, kind=kind, status="running", settings=settings,
                      startedUtc=datetime.now(timezone.utc).isoformat(), recipeId=RECIPE,
                      profileStamp=ready["profileStamp"], runtimeProfileId=PROFILE, source=source,
                      log=f"{kind}-{job_id}.log")
        if kind == "generate":
            record.update(model=model, modelName=model_name)
        job = owned(root, f"jobs/{job_id}.json")
        child = dict(settings, jobId=job_id, **extra)
        for key, stem in (("sourceImage", "source"), ("maskImage", "mask")):
            if key in settings:
                # Work from private copies so the job stays reproducible if the originals move or change.
                name = stem + Path(settings[key]).suffix.lower()
                copy = owned(root, f"artifacts/{job_id}/{name}")
                copy.parent.mkdir(parents=True, exist_ok=key != "sourceImage")
                shutil.copyfile(settings[key], copy)
                settings[key] = f"{job_id}/{name}"
                child[key] = str(copy)
        atomic_json(job, record)
        try:
            result = inference(app, root, kind, child, cancel, progress, log_id=job_id)
            check_cancel(cancel)
            record.update(status="succeeded", completedUtc=datetime.now(timezone.utc).isoformat(), **result)
            settings.update(width=result.get("width", settings.get("width")), height=result.get("height", settings.get("height")))
            image = artifact_image(root, record)
            if image is None:
                raise SetupError("The inference child reported an image outside this job's artifact folder.")
            atomic_json(job, record)
            return dict(record, imagePath=str(image), settings=resolve_settings(root, record))
        except Exception as exc:
            record.update(status="cancelled" if isinstance(exc, Cancelled) else "failed", message=str(exc))
            atomic_json(job, record)
            raise


def artifact_image(root, record):
    """Resolve a job's image inside `artifacts/`, or None.

    New records store `imagePath` relative to `artifacts/` (`<job id>/image.png`), so the data
    folder can move. Records from 0.2.0-alpha.1 stored an absolute path; those are re-rooted
    by their trailing `<job id>/<file>` so a moved data folder still lists them.
    """
    value = record.get("imagePath")
    if not isinstance(value, str) or not value or not job_id_valid(record.get("id")):
        return None
    relative = Path(value)
    if relative.is_absolute():
        relative = Path(relative.parent.name, relative.name)
    if relative.parts[:1] != (record["id"],):
        return None
    artifacts = owned(root, "artifacts").resolve()
    image = (artifacts / relative).resolve()
    if not image.is_relative_to(artifacts / record["id"]) or not image.is_file():
        return None
    return image


def job_log(root, record):
    """The generation's own child log, if it exists; the name is fixed by the job ID, never taken from the record."""
    if not job_id_valid(record.get("id")) or record.get("log") not in (f"generate-{record['id']}.log", f"upscale-{record['id']}.log"):
        return None
    path = owned(root, "logs") / record["log"]
    return path if path.is_file() else None


def library(payload, cancel=None):
    """Paged, searchable successful images plus recent unfinished jobs. JSON remains authoritative."""
    from library_index import listing
    return listing(payload, cancel)
