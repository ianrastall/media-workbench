"""Exports preserve originals and existing destinations until a verified, complete copy is ready."""
import hashlib
import ctypes
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import artifact_export
import inference_setup as setup


class ArtifactExportTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.root = self.folder / "data"
        self.job_id = "a" * 32
        self.image = self.root / "artifacts" / self.job_id / "image.png"
        self.image.parent.mkdir(parents=True)
        self.original = b"original PNG contents" * 100000  # Multiple streaming blocks, no image library.
        self.image.write_bytes(self.original)
        self.record = dict(id=self.job_id, status="succeeded", imagePath=f"{self.job_id}/image.png",
                           imageSha256=hashlib.sha256(self.original).hexdigest())
        self.job = self.root / "jobs" / f"{self.job_id}.json"
        setup.atomic_json(self.job, self.record)
        self.destination = self.folder / "copy café 森林.png"
        self.cancel = threading.Event()
        self.progress = []

    def export(self, **changes):
        payload = dict(dataRoot=str(self.root), jobId=self.job_id, destination=str(self.destination))
        payload.update(changes)
        return artifact_export.export(ROOT, payload, self.cancel, self.progress.append)

    def assert_no_partial(self):
        self.assertEqual(list(self.folder.glob(".media-workbench-*.partial")), [])

    def test_copy_is_byte_identical_and_records_and_original_are_untouched(self):
        original_record = self.job.read_bytes()
        result = self.export()
        self.assertEqual(result, dict(path=str(self.destination), sha256=self.record["imageSha256"]))
        self.assertEqual(self.destination.read_bytes(), self.original)
        self.assertEqual(self.image.read_bytes(), self.original)
        self.assertEqual(self.job.read_bytes(), original_record)
        self.assertEqual(self.progress[-1]["completed"], len(self.original))
        self.assert_no_partial()

    def test_existing_destination_is_replaced_only_after_verification(self):
        self.destination.write_bytes(b"previous picture")
        self.export()
        self.assertEqual(self.destination.read_bytes(), self.original)
        self.assert_no_partial()

    def test_changed_original_never_replaces_the_destination(self):
        self.destination.write_bytes(b"previous picture")
        self.image.write_bytes(b"corrupted original")
        with self.assertRaisesRegex(setup.SetupError, "changed since"):
            self.export()
        self.assertEqual(self.destination.read_bytes(), b"previous picture")
        self.assert_no_partial()

    def test_cancelled_copy_preserves_existing_destination_and_cleans_temporary(self):
        self.destination.write_bytes(b"previous picture")
        def cancel_after_block(_):
            self.cancel.set()
        with self.assertRaises(setup.Cancelled):
            artifact_export.export(ROOT, dict(dataRoot=str(self.root), jobId=self.job_id, destination=str(self.destination)), self.cancel, cancel_after_block)
        self.assertEqual(self.destination.read_bytes(), b"previous picture")
        self.assert_no_partial()

    def test_replace_failure_cleans_temporary_and_preserves_original_files(self):
        self.destination.mkdir()
        (self.destination / "kept.txt").write_bytes(b"previous content")
        with self.assertRaises(setup.SetupError):
            self.export()
        self.assertEqual((self.destination / "kept.txt").read_bytes(), b"previous content")
        self.assertEqual(self.image.read_bytes(), self.original)
        self.assert_no_partial()

    def test_copies_cannot_overwrite_any_managed_data(self):
        for target in (self.image, self.root / "new.png", self.root / "jobs" / "record.png"):
            with self.subTest(target=target), self.assertRaisesRegex(ValueError, "outside"):
                self.export(destination=str(target))
        self.assertEqual(self.image.read_bytes(), self.original)

    def test_missing_damaged_running_and_foreign_records_are_rejected(self):
        for changes in (dict(status="running"), dict(id="b" * 32), dict(imagePath=f"{self.job_id}/../../outside.png"),
                        dict(imageSha256="missing")):
            setup.atomic_json(self.job, dict(self.record, **changes))
            with self.subTest(changes=changes), self.assertRaises(setup.SetupError):
                self.export()
        self.job.write_text("{broken", encoding="utf-8")
        with self.assertRaises(setup.SetupError):
            self.export()
        self.job.unlink()
        with self.assertRaises(setup.SetupError):
            self.export()
        self.assertFalse(self.destination.exists())
        self.assert_no_partial()

    def test_invalid_requests_do_not_create_an_output(self):
        for changes in (dict(jobId="../escape"), dict(destination="relative.png"), dict(destination=str(self.folder / "bad.jpg")),
                        dict(destination=str(self.folder / "missing" / "picture.png"))):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.export(**changes)
        self.assertFalse(self.destination.exists())

    def test_moved_folder_and_legacy_absolute_image_records_can_export(self):
        self.record["imagePath"] = str(self.image)
        setup.atomic_json(self.job, self.record)
        moved = self.folder / "moved"
        self.root.rename(moved)
        self.export(dataRoot=str(moved))
        self.assertEqual(self.destination.read_bytes(), self.original)

    def test_progress_exception_is_contained_and_cleans_native_temporary(self):
        self.destination.write_bytes(b"kept")
        def failed_progress(update):
            if update["completed"]:
                raise RuntimeError("progress consumer failed")
        with self.assertRaisesRegex(RuntimeError, "progress consumer failed"):
            artifact_export.export(ROOT, dict(dataRoot=str(self.root), jobId=self.job_id, destination=str(self.destination)), self.cancel, failed_progress)
        self.assertEqual(self.destination.read_bytes(), b"kept")
        self.assert_no_partial()

    def native(self, destination=None, capacity=64):
        core = ctypes.CDLL(str(setup.native_core_path(ROOT)))
        function = core.mw_export_artifact
        function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p,
                             ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
        function.restype = ctypes.c_int32
        request = json.dumps(dict(dataRoot=str(self.root), jobId=self.job_id, destination=str(destination or self.destination))).encode()
        output = ctypes.create_string_buffer(capacity) if capacity else None
        written = ctypes.c_size_t()
        code = function(request, len(request), None, None, output, capacity, ctypes.byref(written))
        return code, written.value

    def test_native_size_query_and_small_buffer_never_export(self):
        for capacity in (0, 63):
            self.assertEqual(self.native(capacity=capacity), (2, 64))
        self.assertFalse(self.destination.exists())
        self.assert_no_partial()

    def test_direct_native_client_cannot_skip_containment_or_job_state(self):
        # No Python preflight: these exercise the native boundary a future tool client can call.
        for target in (self.image, self.root / "new.png", Path(str(self.root).upper()) / "new.png"):
            self.assertEqual(self.native(target)[0], 1)
        for changes in (dict(status="running"), dict(id="b" * 32), dict(imagePath=f"{self.job_id}/../../outside.png")):
            setup.atomic_json(self.job, dict(self.record, **changes))
            self.assertEqual(self.native()[0], 1)
        self.assertEqual(self.image.read_bytes(), self.original)
        self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    unittest.main()
