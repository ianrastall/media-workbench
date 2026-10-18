"""Thin native pinned-file verification adapter; never changes installation readiness."""
import ctypes
import json
import time

from inference_setup import Cancelled, SetupError, check_cancel, native_core_path


def verify(app, root, path, entry, cancel, progress, label):
    check_cancel(cancel)
    library = native_core_path(app)
    if library is None:
        raise SetupError("The native file verifier is missing. Reinstall Media Workbench.")
    core = ctypes.CDLL(str(library))
    core.mw_abi_version.restype = ctypes.c_uint32
    if core.mw_abi_version() != 1 or not hasattr(core, "mw_verify_file"):
        raise SetupError("The native file verifier is incompatible. Reinstall Media Workbench.")
    callback_type = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_void_p)
    failures = []
    last_report = 0.0

    @callback_type
    def on_progress(completed, total, _context):
        nonlocal last_report
        try:
            if cancel.is_set():
                return 1
            now = time.monotonic()
            if completed == 0 or completed == total or now - last_report >= 0.2:
                progress(dict(stage="Verifying " + label, completed=completed, total=total, unit="bytes"))
                last_report = now
            return int(cancel.is_set())
        except BaseException as exc:
            failures.append(exc)
            return 1

    function = core.mw_verify_file
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, callback_type, ctypes.c_void_p,
                         ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    request = json.dumps(dict(dataRoot=str(root), relativePath=path.relative_to(root).as_posix(),
                              size=entry["size"], sha256=entry["sha256"])).encode("utf-8")
    required = ctypes.c_size_t()
    output = ctypes.create_string_buffer(64)
    code = function(request, len(request), on_progress, None, output, len(output), ctypes.byref(required))
    if failures:
        raise failures[0]
    if code == 5:
        raise Cancelled("Model verification was cancelled. Downloaded files are retained for retry.")
    if code != 0 or required.value != 64 or output.raw.decode("ascii") != entry["sha256"]:
        raise SetupError("Model verification failed: " + label +
                         f". Install / verify again. Native file verifier status: {code}.")
