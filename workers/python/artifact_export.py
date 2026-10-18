"""Friendly export diagnostics and protocol progress; Rust owns validation and verified copying."""
import ctypes
import json
from pathlib import Path

import inference_setup as setup
import video


def export(app, payload, cancel, progress):
    job_id = payload.get("jobId")
    destination = payload.get("destination")
    if not setup.job_id_valid(job_id):
        raise ValueError("Choose an image from Library to save")
    if not isinstance(destination, str) or not Path(destination).is_absolute():
        raise ValueError("Choose a full destination path for the image")
    target = Path(destination).resolve()
    if target.suffix.lower() not in (".png", ".mp4"):
        raise ValueError("Save the image as a PNG file, or a video as an MP4 file")
    if not target.parent.is_dir():
        raise ValueError("The destination folder is no longer available")
    setup.check_cancel(cancel)
    root = setup.data_root(payload)
    if target.is_relative_to(root):
        raise ValueError("Save the copy outside the Media Workbench data folder to keep its records and originals intact")
    record = setup.read_record(setup.owned(root, f"jobs/{job_id}.json"))
    if not record or record.get("id") != job_id or record.get("status") != "succeeded":
        raise setup.SetupError("This image's completed job record is no longer available. Refresh Library.")
    moving = target.suffix.lower() == ".mp4"
    if moving != (record.get("kind") == "video"):
        raise ValueError("Save a video as an MP4 file and a picture as a PNG file")
    image = video.artifact_video(root, record) if moving else setup.artifact_image(root, record)
    if image is None:
        raise setup.SetupError("This image is no longer available. Refresh Library.")
    expected = record.get("videoSha256" if moving else "imageSha256")
    if not isinstance(expected, str) or len(expected) != 64 or any(c not in "0123456789abcdef" for c in expected):
        raise setup.SetupError("This image has no valid recorded checksum; a verified copy cannot be saved.")
    path = setup.native_core_path(app)
    if path is None:
        raise setup.SetupError("The native export service is missing. Reinstall Media Workbench.")
    core = ctypes.CDLL(str(path))
    core.mw_abi_version.restype = ctypes.c_uint32
    if core.mw_abi_version() != 1 or not hasattr(core, "mw_export_artifact"):
        raise setup.SetupError("The native export service is incompatible. Reinstall Media Workbench.")
    callback_type = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_void_p)
    failures = []

    @callback_type
    def on_progress(completed, total, _context):
        # Python exceptions must never cross a C callback boundary. Cancel native work,
        # then re-raise the exception in this frame after Rust has cleaned up its temporary.
        try:
            if cancel.is_set():
                return 1
            progress({"stage": "Saving a copy", "completed": completed, "total": total, "unit": "bytes"})
            return int(cancel.is_set())
        except Exception as exc:
            failures.append(exc)
            return 1

    function = core.mw_export_artifact
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, callback_type, ctypes.c_void_p,
                         ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    request = json.dumps(dict(dataRoot=str(root), jobId=job_id, destination=str(target))).encode("utf-8")
    output = ctypes.create_string_buffer(64)
    written = ctypes.c_size_t()
    code = function(request, len(request), on_progress, None, output, 64, ctypes.byref(written))
    if failures:
        raise failures[0]
    if code == 5:
        raise setup.Cancelled("Saving the copy was cancelled; the destination was kept.")
    if code == 6:
        raise setup.SetupError("This image changed since it was generated. The copy was not saved; the destination was kept.")
    if code != 0 or written.value != 64:
        raise setup.SetupError(f"The native export service could not save the copy ({code}). Check the original and destination folder access. The destination was kept.")
    return {"path": str(target), "sha256": output.raw.decode("ascii")}
