"""Data-folder storage: what each part uses, automatic clean-up, and user-requested removal of reproducible parts.

Rust owns inventory and automatic clean-up; this thin adapter keeps worker clients on the same native implementation.
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


CALLBACK = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)
BUSY = 7


def library(app, name):
    """Load only the app-owned Rust core and require the expected ABI/export."""
    path = native_core_path(app)
    if path is None:
        raise SetupError("The native storage service is missing. Reinstall Media Workbench.")
    try:
        core = ctypes.CDLL(str(path))
    except OSError as exc:
        raise SetupError(f"The native storage service could not be loaded: {exc}") from None
    core.mw_abi_version.restype = ctypes.c_uint32
    if core.mw_abi_version() != 1 or not hasattr(core, name):
        raise SetupError("The native storage service is incompatible. Reinstall Media Workbench.")
    return core


def native(app, name):
    """A Rust storage service with the shared (input, cancel, context, output, capacity, required) shape."""
    function = getattr(library(app, name), name)
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, CALLBACK, ctypes.c_void_p,
                         ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    return function


def drive_info(app, root):
    """Rust's quota-aware free space and drive type; a missing folder is never created."""
    function = library(Path(app), "mw_drive_info").mw_drive_info
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t,
                         ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    request = json.dumps(dict(dataRoot=str(root))).encode("utf-8")
    required = ctypes.c_size_t()
    code = function(request, len(request), None, 0, ctypes.byref(required))
    if code != 2 or not 0 < required.value <= 65536:
        raise SetupError(f"The native drive service could not size its response ({code}).")
    output = ctypes.create_string_buffer(required.value)
    code = function(request, len(request), output, len(output), ctypes.byref(required))
    if code == 1:
        raise ValueError("Choose an absolute data folder inside a drive.")
    if code != 0 or required.value > len(output):
        raise SetupError(f"The data folder's drive could not be read ({code}). Check folder access.")
    return json.loads(output.raw[:required.value])


def call(function, request, cancel):
    """Sizes and runs one call; returns (status, decoded JSON or None). Callback exceptions are re-raised."""
    failures = []

    @CALLBACK
    def on_cancel(_context):
        try:
            return int(cancel.is_set())
        except Exception as exc:
            failures.append(exc)
            return 1

    required = ctypes.c_size_t()
    code = function(request, len(request), on_cancel, None, None, 0, ctypes.byref(required))
    if code != 2 or not 0 < required.value <= 1024 * 1024:
        raise SetupError(f"The native storage service could not size its response ({code}).")
    output = ctypes.create_string_buffer(required.value)
    code = function(request, len(request), on_cancel, None, output, len(output), ctypes.byref(required))
    if failures:
        raise failures[0]
    if code != 0 or required.value > len(output):
        return code, None
    return code, json.loads(output.raw[:required.value])


def summary(payload, cancel=None, app=None):
    cancel = cancel if cancel is not None else threading.Event()
    check_cancel(cancel)
    app = Path(app) if app is not None else Path(__file__).resolve().parents[2]
    function = native(app, "mw_storage_summary")
    layout = json.loads((app / "profiles/storage-layout.json").read_text(encoding="utf-8"))
    request = json.dumps(dict(dataRoot=payload.get("dataRoot"), **layout)).encode("utf-8")
    code, result = call(function, request, cancel)
    if code == 5:
        raise Cancelled("Storage measurement was cancelled.")
    if code == 1:
        raise ValueError("The data folder or storage layout is invalid, or a managed path passes through a link or junction.")
    if code != 0:
        raise SetupError(f"The native storage service could not read the folder ({code}). Check folder access; no partial sizes were reported.")
    return result


def tidy(app, root, verified, remove_caches, cancel=None):
    """Rust's automatic clean-up under the data-folder lock (old logs, old unfinished-job records,
    stale downloads, and the download caches once setup is verified). Returns its report."""
    cancel = cancel if cancel is not None else threading.Event()
    check_cancel(cancel)
    app = Path(app)
    layout = json.loads((app / "profiles/storage-layout.json").read_text(encoding="utf-8"))
    request = json.dumps(dict(dataRoot=str(root), installationVerified=bool(verified),
                              removeDownloadCaches=bool(verified and remove_caches), **layout)).encode("utf-8")
    code, result = call(native(app, "mw_tidy_data_folder"), request, cancel)
    if code == BUSY:
        raise SetupError("Another operation is using this data folder")
    if code == 5:
        raise Cancelled("Clean-up was cancelled. Nothing it had started is left half-removed.")
    if code == 1:
        raise ValueError("The data folder is invalid, or a managed path passes through a link or junction.")
    if code != 0:
        raise SetupError(f"The native clean-up could not finish ({code}). Check folder access.")
    return result


def after_setup(app, payload, result, cancel, progress):
    """Once setup passed its test, remove what it no longer needs; keepDownloadCaches keeps the caches.

    A verified installation never fails because clean-up could not run; the next start tries again.
    """
    if result.get("status") != "ready":
        return result
    progress({"stage": "Removing setup downloads that are no longer needed"})
    try:
        report = tidy(app, data_root(payload), True, payload.get("keepDownloadCaches") is not True, cancel)
    except (SetupError, ValueError, OSError):
        return dict(result, cleanedBytes=0)
    return dict(result, cleanedBytes=report["removedBytes"])


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
