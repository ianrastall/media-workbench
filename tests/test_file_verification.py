"""Real native verification and setup integration, without AI downloads or runtimes."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import file_verification
import inference_setup as setup
from test_inference_setup import fake_app, ready_install


class FileVerificationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="MediaWorkbench-verify-café-森林-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.path = self.root / "models/test/weights.bin"
        self.path.parent.mkdir(parents=True)
        self.contents = b"pinned weights" * 200000
        self.path.write_bytes(self.contents)
        self.entry = dict(size=len(self.contents), sha256=hashlib.sha256(self.contents).hexdigest())
        self.cancel = threading.Event()
        self.progress = []

    def verify(self, **changes):
        file_verification.verify(ROOT, self.root, self.path, dict(self.entry, **changes),
                                 self.cancel, self.progress.append, "weights")

    def test_correct_bytes_and_unicode_paths_preserve_file_and_ready_record(self):
        setup.atomic_json(setup.state_path(self.root), {"status": "ready", "evidence": "kept"})
        record = setup.state_path(self.root).read_bytes()
        stamp = setup.file_stamp(self.path)
        self.verify()
        self.assertEqual(setup.file_stamp(self.path), stamp)
        self.assertEqual(self.path.read_bytes(), self.contents)
        self.assertEqual(setup.state_path(self.root).read_bytes(), record)
        self.assertEqual(self.progress[0]["completed"], 0)
        self.assertEqual(self.progress[-1]["completed"], len(self.contents))
        self.assertEqual(self.progress[-1]["unit"], "bytes")

    def test_size_and_same_size_corruption_are_rejected(self):
        for change in [dict(size=self.entry["size"] + 1), dict(sha256="0" * 64)]:
            with self.subTest(change=change), self.assertRaisesRegex(setup.SetupError, "status: 6"):
                self.verify(**change)
        self.assertFalse(setup.state_path(self.root).exists())

    def test_cancellation_within_large_file_preserves_downloads(self):
        # Control only the reporting clock so the first streaming block is observable.
        def cancel_during_read(progress):
            if progress["completed"] > 0:
                self.cancel.set()
        with patch.object(file_verification.time, "monotonic", side_effect=range(1000)):
            with self.assertRaises(setup.Cancelled):
                file_verification.verify(ROOT, self.root, self.path, self.entry,
                                         self.cancel, cancel_during_read, "weights")
        self.assertEqual(self.path.read_bytes(), self.contents)
        self.assertFalse(setup.state_path(self.root).exists())

    def test_callback_exception_is_contained_and_rethrown_in_python(self):
        def broken(_):
            raise RuntimeError("progress failed")
        with self.assertRaisesRegex(RuntimeError, "progress failed"):
            file_verification.verify(ROOT, self.root, self.path, self.entry, self.cancel, broken, "weights")
        self.assertEqual(self.path.read_bytes(), self.contents)

    def test_missing_service_and_missing_file_fail_closed(self):
        with patch.object(file_verification, "native_core_path", return_value=None):
            with self.assertRaisesRegex(setup.SetupError, "verifier is missing"):
                self.verify()
        self.path.unlink()
        with self.assertRaisesRegex(setup.SetupError, "verification failed"):
            self.verify()

    @unittest.skipUnless(os.name == "nt", "Windows file sharing")
    def test_writes_and_replacement_are_refused_while_hashing(self):
        attempts = []
        def attempt_write(progress):
            if progress["completed"] > 0 and not attempts:
                with self.assertRaises(PermissionError):
                    self.path.write_bytes(b"changed")
                with self.assertRaises(PermissionError):
                    self.path.unlink()
                attempts.append(True)
        with patch.object(file_verification.time, "monotonic", side_effect=range(1000)):
            file_verification.verify(ROOT, self.root, self.path, self.entry, self.cancel, attempt_write, "weights")
        self.assertEqual(attempts, [True])
        self.assertEqual(self.path.read_bytes(), self.contents)

    @unittest.skipUnless(os.name == "nt", "Windows junction containment")
    def test_managed_folder_junction_is_rejected(self):
        import _winapi
        link = self.root / "linked"
        _winapi.CreateJunction(str(self.path.parent), str(link))
        with self.assertRaisesRegex(setup.SetupError, "status: 1"):
            file_verification.verify(ROOT, self.root, link / self.path.name, self.entry,
                                     self.cancel, self.progress.append, "linked weights")

    def test_abi_rejects_unsafe_declarations_without_writing_output(self):
        core = ctypes.CDLL(str(setup.native_core_path(ROOT)))
        callback_type = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_void_p)
        callback = callback_type(lambda *_: 0)
        function = core.mw_verify_file
        function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, callback_type, ctypes.c_void_p,
                             ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
        function.restype = ctypes.c_int32
        request = dict(dataRoot=str(self.root), relativePath=self.path.relative_to(self.root).as_posix(), **self.entry)
        for change in [dict(relativePath="../escape"), dict(relativePath="NUL.txt"),
                       dict(dataRoot=self.root.anchor), dict(size=True), dict(sha256="A" * 64)]:
            data = json.dumps(dict(request, **change)).encode()
            required = ctypes.c_size_t()
            output = ctypes.create_string_buffer(b"x" * 64, 64)
            self.assertEqual(function(data, len(data), callback, None, output, 64, ctypes.byref(required)), 1)
            self.assertEqual(output.raw, b"x" * 64)

    def test_setup_retains_stamp_format_without_python_model_hashing(self):
        app = fake_app(self.root)
        data = self.root / "data"
        with patch.object(setup, "digest", side_effect=AssertionError("Python hashed a model")):
            ready_install(app, data)
        record = json.loads(setup.state_path(data).read_text())
        self.assertTrue(setup.files_unchanged(app, data, record))
        self.assertEqual(set(next(iter(record["files"].values()))), {"size", "mtimeNs"})
