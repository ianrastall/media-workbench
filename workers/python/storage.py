"""Data-folder storage: what each part uses, and user-requested removal of reproducible parts.

Rust owns inventory; this thin adapter keeps worker clients on the same native implementation.
Removal holds the data-folder lock, so it never races setup or generation.
"""
import ctypes
import json
from pathlib import Path
import shutil
import threading

from inference_setup import (PROFILE, RECIPE, UPSCALER, check_cancel, data_root, exclusive, owned,
                             recover_interrupted, state_path, native_core_path, SetupError, Cancelled)

REMOVABLE = {"caches": ["cache", "bootstrap"], "logs": ["logs"]}
INSTALLATION = [f"runtimes/{PROFILE}", f"models/{RECIPE}", f"models/{UPSCALER}", "cache", "bootstrap"]


def summary(payload, cancel=None, app=None):
    cancel = cancel if cancel is not None else threading.Event()
    check_cancel(cancel)
    app = Path(app) if app is not None else Path(__file__).resolve().parents[2]
    path = native_core_path(app)
    if path is None:
        raise SetupError("The native storage service is missing. Reinstall Media Workbench.")
    core = ctypes.CDLL(str(path))
    core.mw_abi_version.restype = ctypes.c_uint32
    if core.mw_abi_version() != 1 or not hasattr(core, "mw_storage_summary"):
        raise SetupError("The native storage service is incompatible. Reinstall Media Workbench.")
    callback_type = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)
    failures = []

    @callback_type
    def on_cancel(_context):
        try:
            return int(cancel.is_set())
        except Exception as exc:
            failures.append(exc)
            return 1

    function = core.mw_storage_summary
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, callback_type, ctypes.c_void_p,
                         ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    layout = json.loads((app / "profiles/storage-layout.json").read_text(encoding="utf-8"))
    request = json.dumps(dict(dataRoot=payload.get("dataRoot"), **layout)).encode("utf-8")
    required = ctypes.c_size_t()
    code = function(request, len(request), on_cancel, None, None, 0, ctypes.byref(required))
    if code != 2 or not 0 < required.value <= 1024 * 1024:
        raise SetupError(f"The native storage service could not size its response ({code}).")
    output = ctypes.create_string_buffer(required.value)
    code = function(request, len(request), on_cancel, None, output, len(output), ctypes.byref(required))
    if failures:
        raise failures[0]
    if code == 5:
        raise Cancelled("Storage measurement was cancelled.")
    if code == 1:
        raise ValueError("The data folder or storage layout is invalid, or a managed path passes through a link or junction.")
    if code != 0 or required.value > len(output):
        raise SetupError(f"The native storage service could not read the folder ({code}). Check folder access; no partial sizes were reported.")
    return json.loads(output.raw[:required.value])


def remove(root, folders, cancel, progress):
    for folder in folders:
        check_cancel(cancel)
        path = owned(root, folder)  # Refuses a link or junction on the way to the target.
        if path.is_dir():
            progress({"stage": "Removing " + folder})
            shutil.rmtree(path)  # Does not follow junctions inside the tree.


def clear(payload, cancel, progress, app=None):
    area = payload.get("area")
    root = data_root(payload)
    if area == "installation":
        folders = INSTALLATION
    else:
        if area not in REMOVABLE:
            raise ValueError("Choose download caches, logs, or the installation to remove")
        folders = REMOVABLE[area]
    with exclusive(root):
        recover_interrupted(root)
        if area == "installation":
            state_path(root).unlink(missing_ok=True)  # First, so a partial removal can never read as ready.
        remove(root, folders, cancel, progress)
    return summary(payload, cancel, app)
