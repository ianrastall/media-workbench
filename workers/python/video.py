"""Short videos: AnimateDiff's motion layers beside the verified Stable Diffusion 1.5 installation.

Standard library only. The motion adapter is pinned by revision, size, and SHA-256, verified through
Rust, and must pass its own real GPU test (a 16-frame clip, encoded to MP4) before videos can be made.
The ML child writes raw frames; Rust's mw_encode_video turns them into an MP4 with Windows' own encoder.
"""
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import shutil
import threading
import uuid

import hardware
import inference_setup as setup

MOTION = "animatediff-v1-5-3"
# Bump when what a verified video setup means changes (pipeline, scheduler, test, or encoding).
ADAPTER_VERSION = 1
FPS = 8
FRAME_CHOICES = (16, 24)
TEST = dict(prompt="A small red ceramic house beside a green tree, gentle wind, watercolor illustration, daylight",
            negativePrompt="", seed=20261018, steps=4, width=512, height=512, frames=16, guidanceScale=7.5)
STAMPED_FILES = ["profiles/video-files.json", "profiles/sd15-cu126.lock"]
SETUP_HEADROOM_BYTES = 2 * 1024**3
VIDEO_MAX_BYTES = 64 * 1024**2  # An MP4 at the encoder's highest bitrate for the longest clip, with room to spare.
ENCODER_MISSING = 8
ENCODER_MESSAGE = ("Windows' video encoder is not available on this computer. On a Windows N edition, add the Media "
                   "Feature Pack (Settings > Apps > Optional features), then select Set up videos again.")
NOT_READY = "Set up videos in Create first; they need their own download and test on your GPU."


class EncoderMissing(setup.SetupError):
    """Windows has no H.264 encoder; no runtime choice can fix that."""


def state_path(root):
    return setup.owned(root, "jobs/install-video.json")


def manifest(app):
    return json.loads((app / "profiles/video-files.json").read_text(encoding="utf-8"))


def model_files(app, root):
    """(stamp key, path, manifest entry) for each pinned motion-adapter file."""
    folder = setup.owned(root, f"models/{MOTION}")
    return [(f"{MOTION}/{entry['name']}", setup.owned(folder, entry["name"]), entry) for entry in manifest(app)["files"]]


def profile_stamp(app, runtime):
    """Identity of a verified video setup: adapter version, runtime choice, pinned files, and the shared runtime lock."""
    settings = hardware.VIDEO_RUNTIMES[runtime]
    lines = [f"video-adapter={ADAPTER_VERSION}", f"runtime={runtime}:{settings['precision']}:offload={settings['offload']}"]
    for name in STAMPED_FILES:
        content = (app / name).read_bytes().replace(b"\r\n", b"\n")
        lines.append(f"{name}={hashlib.sha256(content).hexdigest()}")
    return hashlib.sha256("\n".join(lines).encode()).hexdigest()


def stamp_valid(app, record):
    runtime = record.get("runtime")
    return runtime in hardware.VIDEO_RUNTIMES and record.get("profileStamp") == profile_stamp(app, runtime)


def files_unchanged(app, root, record):
    stamps = record.get("files")
    if not isinstance(stamps, dict):
        return False
    for key, path, _ in model_files(app, root):
        try:
            if setup.file_stamp(path) != stamps.get(key):
                return False
        except OSError:
            return False
    return True


def base_ready(app, root):
    record = setup.read_record(setup.state_path(root)) if setup.state_path(root).is_file() else None
    return bool(record) and setup.checked_installation(app, root, record).get("status") == "ready"


def checked(app, root, record):
    """Read-only readiness projection: videos also need a ready Stable Diffusion 1.5 installation."""
    record = dict(record)
    if record.get("status") != "ready":
        return record
    if not stamp_valid(app, record):
        record.update(status="needs_verification", message="Video settings changed in this version. Select Set up videos to test them again.")
    elif not files_unchanged(app, root, record):
        record.update(status="needs_verification", message="The motion adapter's files are missing or changed. Select Set up videos again.")
    elif not base_ready(app, root):
        record.update(status="needs_verification", message="Stable Diffusion 1.5 needs Install / verify before videos can be made.")
    return record


def public(record):
    record = {key: value for key, value in record.items() if key != "files"}
    runtime = record.get("runtime")
    if runtime in hardware.VIDEO_RUNTIMES:
        record["settings"] = dict(id=runtime, **{key: hardware.VIDEO_RUNTIMES[runtime][key] for key in ("label", "description", "precision")})
    return record


def status(app, payload):
    root = setup.data_root(payload)
    setup.recover_if_idle(root)
    path = state_path(root)
    if not path.is_file():
        return {"status": "not_installed", "message": "Videos are not set up yet. Setup downloads 1.7 GB and tests a short clip on your GPU."}
    record = setup.read_record(path)
    if record is None:
        return {"status": "needs_verification", "message": "The video setup record is damaged. Select Set up videos again."}
    return public(checked(app, root, record))


def ready_record(app, root):
    """The verified video setup, or a plain SetupError naming what to do."""
    path = state_path(root)
    record = setup.read_record(path) if path.is_file() else None
    if not record:
        raise setup.SetupError(NOT_READY)
    record = checked(app, root, record)
    if record.get("status") != "ready":
        raise setup.SetupError(record.get("message") or NOT_READY)
    return record


def child_payload(app, root):
    return dict(runtime=ready_record(app, root)["runtime"])


def required_free_bytes(app, root):
    missing = sum(entry["size"] for _, path, entry in model_files(app, root)
                  if not path.is_file() or path.stat().st_size != entry["size"])
    # The test clip's raw frames and MP4 are written too, then removed.
    return missing + TEST["width"] * TEST["height"] * 3 * TEST["frames"] + SETUP_HEADROOM_BYTES


def declined_here(item, gpu, runtime):
    return (item.get("runtime") == runtime and item.get("gpu") == gpu.get("name")
            and item.get("driver") == gpu.get("driver") and item.get("adapter") == ADAPTER_VERSION)


def encode(app, frames_path, width, height, frames, destination, cancel):
    """Rust's mw_encode_video; returns the MP4's SHA-256. Cancellable; leaves no partial file."""
    from file_verification import load_core
    function = load_core(app, "mw_encode_video").mw_encode_video
    callback_type = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, callback_type, ctypes.c_void_p,
                         ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    failures = []

    @callback_type
    def on_cancel(_context):
        try:
            return int(cancel.is_set())
        except BaseException as exc:
            failures.append(exc)
            return 1

    request = json.dumps(dict(framesPath=str(frames_path), width=width, height=height, frames=frames, fps=FPS,
                              destination=str(destination))).encode("utf-8")
    required = ctypes.c_size_t()
    output = ctypes.create_string_buffer(64)
    code = function(request, len(request), on_cancel, None, output, len(output), ctypes.byref(required))
    if failures:
        raise failures[0]
    if code == 5:
        raise setup.Cancelled("Making the video was cancelled; nothing was added to Library.")
    if code == ENCODER_MISSING:
        raise EncoderMissing(ENCODER_MESSAGE)
    if code != 0 or required.value != 64:
        raise setup.SetupError(f"The video could not be written ({code}). Check that the data folder's drive has space and is writable.")
    return output.raw.decode("ascii")


def test_runtime(app, root, gpu, chosen, declined, cancel, progress):
    """The real test: a 16-frame clip on the GPU, then MP4 encoding; falls back to the conservative choice."""
    first = [] if any(declined_here(item, gpu, chosen) for item in declined) else [chosen]
    candidates = first + ([hardware.VIDEO_CONSERVATIVE] if chosen != hardware.VIDEO_CONSERVATIVE else [])
    for runtime in candidates:
        label = hardware.VIDEO_RUNTIMES[runtime]["label"]
        progress({"stage": f"Testing a short video on your GPU with {label.lower()} settings"})
        folder = setup.owned(root, f"cache/video-test/{uuid.uuid4().hex}")
        folder.mkdir(parents=True)
        try:
            evidence = setup.inference(app, root, "verify-video", dict(TEST, runtime=runtime, outputFolder=str(folder)), cancel, progress)
            setup.check_cancel(cancel)
            progress({"stage": "Testing Windows' video encoder"})
            encode(app, folder / "frames.rgb", TEST["width"], TEST["height"], TEST["frames"], folder / "test.mp4", cancel)
            return runtime, dict(evidence, runtime=runtime), declined
        except (setup.Cancelled, EncoderMissing):
            raise
        except setup.SetupError as exc:
            if runtime == candidates[-1]:
                raise
            declined = [item for item in declined if not declined_here(item, gpu, runtime)] + [dict(
                runtime=runtime, gpu=gpu.get("name"), driver=gpu.get("driver"), adapter=ADAPTER_VERSION,
                message=str(exc)[:500], failedUtc=datetime.now(timezone.utc).isoformat())]
            progress({"stage": f"{label} settings did not pass; testing memory-saving settings"})
        finally:
            shutil.rmtree(folder, ignore_errors=True)
    raise setup.SetupError("No video runtime choice was tested.")


def install(app, payload, cancel, progress):
    """Download, verify, and GPU-test the motion adapter; ready is written last, atomically."""
    from file_verification import verify
    root = setup.data_root(payload)
    with setup.exclusive(root):
        setup.recover_interrupted(root)
        if not base_ready(app, root):
            raise setup.SetupError("Set up Stable Diffusion 1.5 first (Install / verify in Create); videos use its verified runtime and model.")
        previous = setup.read_record(state_path(root)) if state_path(root).is_file() else None
        declined = [item for item in (previous or {}).get("declinedRuntimes") or [] if isinstance(item, dict)]
        record = dict(schemaVersion=1, attemptId=uuid.uuid4().hex, status="preparing", adapter=ADAPTER_VERSION,
                      startedUtc=datetime.now(timezone.utc).isoformat(), source={k: manifest(app)[k] for k in ("repoId", "revision", "license")})

        def stage(name, message):
            record.update(status=name, message=message)
            setup.atomic_json(state_path(root), record)
            progress({"stage": message})
        try:
            stage("preparing", "Checking free space and the graphics card for videos")
            needed = required_free_bytes(app, root)
            free = shutil.disk_usage(root).free
            if free < needed:
                raise setup.SetupError(f"Setting up videos needs about {setup.size_text(needed)} free on the data folder's drive; "
                                       f"{setup.size_text(free)} is free. Free up space or move the data folder in Settings.")
            found = hardware.detect(root, app)
            choice = hardware.choose_video(found, setup.minimum_vram_mib(app))
            if choice["blocked"]:
                raise setup.SetupError(choice["blocked"])
            stage("downloading", "Fetching the AnimateDiff motion adapter")
            for _, path, entry in model_files(app, root):
                setup.download(entry, path, cancel, progress)
            stage("verifying", "Verifying the motion adapter's checksum")
            stamps = {}
            for key, path, entry in model_files(app, root):
                before = setup.file_stamp(path)
                verify(app, root, path, entry, cancel, progress, key)
                if setup.file_stamp(path) != before:
                    raise setup.SetupError("The motion adapter changed while it was checked: " + key + ". Set up videos again.")
                stamps[key] = before
            stage("verifying", "Testing a short video on your GPU")
            runtime, evidence, declined = test_runtime(app, root, found["gpu"], choice["runtime"], declined, cancel, progress)
            settings = hardware.VIDEO_RUNTIMES[runtime]
            message = f"Videos are ready on {evidence['gpu']} with {settings['label'].lower()} settings."
            if runtime != choice["runtime"]:
                message += " Faster settings did not pass their test on this computer."
            record.update(status="ready", message=message, runtime=runtime, declinedRuntimes=declined,
                          profileStamp=profile_stamp(app, runtime), verifiedUtc=datetime.now(timezone.utc).isoformat(),
                          evidence=evidence, files=stamps)
            setup.atomic_json(state_path(root), record)
            return public(record)
        except Exception as exc:
            record.update(status="cancelled" if isinstance(exc, setup.Cancelled) else "failed", message=str(exc))
            setup.atomic_json(state_path(root), record)
            raise


def validate(payload):
    """Video settings in the shape run_job stores; the starting-picture and sampler choices do not apply."""
    frames = payload.get("frames", FRAME_CHOICES[0])
    if type(frames) is not int or frames not in FRAME_CHOICES:
        raise ValueError("Choose a 2-second (16 frames) or 3-second (24 frames) video")
    model = payload.get("model") or setup.RECIPE
    if model != setup.RECIPE:
        raise ValueError("Videos use Stable Diffusion 1.5 for now; models you added make pictures only")
    settings = setup.validate_generation({key: value for key, value in payload.items()
                                          if key not in ("sourceImage", "maskImage", "sampler", "frames", "model")})
    settings.update(frames=frames, fps=FPS, sampler="ddim")
    return settings


def space_needed(settings):
    raw = settings["width"] * settings["height"] * 3 * settings["frames"]
    return raw + VIDEO_MAX_BYTES + settings["width"] * settings["height"] * 3 + setup.WRITE_HEADROOM_BYTES


def finish(app, root, job_id, result, settings, cancel, progress):
    """Encodes the child's frames into video.mp4 and removes them; returns the result with the video's evidence."""
    folder = setup.owned(root, f"artifacts/{job_id}")
    frames = setup.owned(folder, "frames.rgb")
    progress({"stage": "Saving the video"})
    try:
        digest = encode(app, frames, result["width"], result["height"], settings["frames"],
                        setup.owned(folder, "video.mp4"), cancel)
    finally:
        frames.unlink(missing_ok=True)
    return dict(result, videoPath=f"{job_id}/video.mp4", videoSha256=digest, frames=settings["frames"], fps=FPS)


def discard(root, job_id):
    """What an unfinished video job leaves behind: raw frames, its poster, and any partial MP4."""
    if not setup.job_id_valid(job_id):
        return
    folder = setup.owned(root, f"artifacts/{job_id}")
    for name in ("frames.rgb", "frames.rgb.partial", "poster.png", "poster.partial"):
        setup.owned(folder, name).unlink(missing_ok=True)
    if folder.is_dir():
        for partial in folder.glob(".video.mp4.*.partial"):
            partial.unlink(missing_ok=True)


def artifact_video(root, record):
    """A finished job's MP4 inside its own artifact folder, or None."""
    value = record.get("videoPath")
    if not isinstance(value, str) or not setup.job_id_valid(record.get("id")) or value != f"{record['id']}/video.mp4":
        return None
    path = (setup.owned(root, "artifacts") / value).resolve()
    if not path.is_relative_to(setup.owned(root, "artifacts").resolve() / record["id"]) or not path.is_file():
        return None
    return path


def generate(app, payload, cancel, progress):
    settings = validate(payload)  # Before data_root, which creates the folder.
    return setup.run_job(app, setup.data_root(payload), "video", settings, cancel, progress)


def remove(app, payload, cancel=None):
    """Removes the motion adapter and its readiness record; videos already made are kept."""
    cancel = cancel if cancel is not None else threading.Event()
    root = setup.data_root(payload)
    with setup.exclusive(root):
        setup.recover_interrupted(root)
        state_path(root).unlink(missing_ok=True)  # First, so a partial removal never reads as ready.
        setup.check_cancel(cancel)
        folder = setup.owned(root, f"models/{MOTION}")
        if folder.is_dir():
            shutil.rmtree(folder)
    return status(app, payload)
