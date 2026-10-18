"""Short videos: AnimateDiff's motion layers beside the verified Stable Diffusion 1.5 installation.

Standard library only. The motion adapter is pinned by revision, size, and SHA-256, verified through
Rust, and must pass its own real GPU test (a 16-frame clip, encoded to MP4) before videos can be made.
The ML child writes raw frames; Rust's mw_encode_video turns them into an MP4 with Windows' own encoder.
"""
import ctypes
import json
import shutil
import uuid

from addon import Addon, base_ready  # noqa: F401  (base_ready is part of this module's interface)
import hardware
import inference_setup as setup

MOTION = "animatediff-v1-5-3"
FPS = 8
FRAME_CHOICES = (16, 24)
TEST = dict(prompt="A small red ceramic house beside a green tree, gentle wind, watercolor illustration, daylight",
            negativePrompt="", seed=20261018, steps=4, width=512, height=512, frames=16, guidanceScale=7.5)
VIDEO_MAX_BYTES = 64 * 1024**2  # An MP4 at the encoder's highest bitrate for the longest clip, with room to spare.
ENCODER_MISSING = 8
ENCODER_MESSAGE = ("Windows' video encoder is not available on this computer. On a Windows N edition, add the Media "
                   "Feature Pack (Settings > Apps > Optional features), then select Set up videos again.")
MISSING = "Videos are not set up yet. Setup downloads 1.7 GB and tests a short clip on your GPU."
# Bump the adapter version when what a verified video setup means changes (pipeline, scheduler, test, or encoding).
VIDEOS = Addon("video", "short videos", MOTION, "video-files.json", hardware.VIDEO_RUNTIMES, hardware.VIDEO_CONSERVATIVE, 1,
               ["profiles/video-files.json", "profiles/sd15-cu126.lock"], "Set up videos", "the AnimateDiff motion adapter", plural=True)
NOT_READY = VIDEOS.not_ready


class EncoderMissing(setup.SetupError):
    """Windows has no H.264 encoder; no runtime choice can fix that."""


def state_path(root):
    return VIDEOS.state_path(root)


def model_files(app, root):
    return VIDEOS.model_files(app, root)


def status(app, payload):
    return VIDEOS.status(app, payload, MISSING)


def child_payload(app, root):
    return dict(runtime=VIDEOS.ready_record(app, root)["runtime"])


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


def install(app, payload, cancel, progress):
    """Download, verify, and GPU-test the motion adapter: a real 16-frame clip, then MP4 encoding."""
    root = setup.data_root(payload)

    def fetch(_root, files):
        for _, path, entry in files:
            setup.download(entry, path, cancel, progress)

    def attempt(runtime):
        folder = setup.owned(root, f"cache/video-test/{uuid.uuid4().hex}")
        folder.mkdir(parents=True)
        try:
            evidence = setup.inference(app, root, "verify-video", dict(TEST, runtime=runtime, outputFolder=str(folder)), cancel, progress)
            setup.check_cancel(cancel)
            progress({"stage": "Testing Windows' video encoder"})
            encode(app, folder / "frames.rgb", TEST["width"], TEST["height"], TEST["frames"], folder / "test.mp4", cancel)
            return evidence
        finally:
            shutil.rmtree(folder, ignore_errors=True)
    # The test clip's raw frames are written too, then removed.
    raw = TEST["width"] * TEST["height"] * 3 * TEST["frames"]
    return VIDEOS.install(app, payload, cancel, progress, lambda found: hardware.choose_video(found, setup.minimum_vram_mib(app)),
                          raw, fetch, attempt, final=(EncoderMissing,))


def validate(payload):
    """Video settings in the shape run_job stores; the starting-picture and sampler choices do not apply."""
    frames = payload.get("frames", FRAME_CHOICES[0])
    if type(frames) is not int or frames not in FRAME_CHOICES:
        raise ValueError("Choose a 2-second (16 frames) or 3-second (24 frames) video")
    model = payload.get("model") or setup.RECIPE
    if model != setup.RECIPE:
        raise ValueError("Videos use Stable Diffusion 1.5 for now; other models make pictures only")
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
    source = VIDEOS.manifest(app)
    return dict(result, videoPath=f"{job_id}/video.mp4", videoSha256=digest, frames=settings["frames"], fps=FPS,
                motionSource={key: source[key] for key in ("repoId", "revision", "license")})


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
    VIDEOS.remove(app, payload, cancel)
    return status(app, payload)
