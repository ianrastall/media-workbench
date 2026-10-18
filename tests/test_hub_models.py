"""Public Hub download gates with sparse fake weights, real Rust checks, and no network/GPU."""
import copy
import ctypes
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "workers/python"))
import hub_catalog as catalog
import hub_models as hub
import inference_setup as setup
import model_library as models
from test_hardware import machine
from test_inference_setup import fake_app, ready_install, fake_generation
from test_model_library import checkpoint, CHECKPOINT, XL_CHECKPOINT, write_safetensors


class Response:
    def __init__(self, data, start=None, end=None):
        self.stream = data.open("rb") if isinstance(data, Path) else io.BytesIO(data)
        size = self.stream.seek(0, 2)
        self.stream.seek(start or 0)
        self.status = 206 if start is not None else 200
        self.left = end - start + 1 if start is not None else size
        self.headers = {"Content-Length": str(self.left)}
        if start is not None:
            self.headers["Content-Range"] = f"bytes {start}-{end}/{size}"

    def read(self, count):
        result = self.stream.read(min(count, self.left))
        self.left -= len(result)
        return result

    def __enter__(self): return self
    def __exit__(self, *_): self.stream.close()


class HubTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="MediaWorkbench-Hub-森林-")
        self.addCleanup(temp.cleanup)
        self.folder = Path(temp.name).resolve()
        self.app, self.root = fake_app(self.folder), self.folder / "data"
        self.file = checkpoint(self.folder / "source.safetensors")
        self.sha = setup.digest(self.file)
        self.revision = "a" * 40
        self.notice = b"# Model\nLicense: CreativeML Open RAIL-M\n"
        self.data = dict(id="owner/model", sha=self.revision, gated=False, cardData={"license": "openrail"},
                         siblings=[dict(rfilename="model.safetensors", size=self.file.stat().st_size, lfs=dict(sha256=self.sha)),
                                   dict(rfilename="README.md", size=len(self.notice), blobId=hashlib.sha1(
                                       f"blob {len(self.notice)}\0".encode() + self.notice, usedforsecurity=False).hexdigest())])
        self.payload = dict(dataRoot=str(self.root), repoId="owner/model", revision=self.revision, file="model.safetensors",
                            sha256=self.sha, bytes=self.file.stat().st_size, licenseReviewed=True)
        self.cancel = threading.Event()
        self.requests, self.tests = [], []
        for name, value in [("storage.native_core_path", setup.native_core_path(ROOT)),
                            ("file_verification.native_core_path", setup.native_core_path(ROOT)),
                            ("hub_catalog.fetch_json", self.fetch), ("hub_models.open_file", self.open),
                            ("hardware.detect", machine("NVIDIA GeForce GTX 1660 Ti", 6144, "7.5")),
                            ("inference_setup.inference", self.inference)]:
            item = patch(name, side_effect=value) if callable(value) else patch(name, return_value=value)
            item.start(); self.addCleanup(item.stop)
        ready_install(self.app, self.root)

    def fetch(self, path):
        self.requests.append(path)
        return copy.deepcopy(self.data)

    def open(self, repo, revision, path, start=None, end=None):
        self.requests.append((repo, revision, path, start, end))
        return Response(self.file if path == "model.safetensors" else self.notice, start, end)

    def inference(self, app, root, operation, payload, cancel, progress, log_id=None):
        self.tests.append((operation, payload))
        if operation == "generate": return fake_generation(app, root, operation, payload, cancel, progress, log_id)
        return dict(gpu="Test GPU", seconds=1)

    def install(self): return hub.install(self.app, self.payload, self.cancel, lambda _: None)

    def imported(self):
        return list((self.root / "models/imported").glob("*"))

    def test_plan_reads_only_bounded_headers_and_never_marks_a_model_installed(self):
        planned = hub.plan(self.app, self.payload, self.cancel)
        self.assertTrue(planned["canInstall"] and planned["headerChecked"])
        self.assertEqual(planned["selectedFile"]["sha256"], self.sha)
        self.assertFalse((self.root / "models/imported").exists())
        self.assertEqual(self.tests, [])
        reads = [item for item in self.requests if isinstance(item, tuple)]
        self.assertEqual(len(reads), 2)
        self.assertTrue(all(item[1] == self.revision and item[3] is not None for item in reads))

    def test_preflight_of_a_new_folder_creates_nothing(self):
        planned = self.folder / "new" / "data"
        result = hub.plan(self.app, dict(self.payload, dataRoot=str(planned)), self.cancel)
        self.assertFalse(result["canInstall"])
        self.assertFalse(planned.exists())

    def test_pinned_download_passes_native_verification_and_gpu_test_and_preserves_notices_and_provenance(self):
        added = self.install()
        self.assertEqual(added["status"], "ready")
        folder = models.folder_of(self.root, added["id"])
        record = models.read_model(self.root, added["id"])
        self.assertEqual(setup.digest(folder / "model.safetensors"), self.sha)
        self.assertEqual((folder / "notices/README.md").read_bytes(), self.notice)
        self.assertEqual(record["source"]["revision"], self.revision)
        self.assertEqual(self.tests[0][0], "verify-model")
        self.assertFalse(list(folder.rglob("*.partial")))
        picture = setup.generate(self.app, dict(dataRoot=str(self.root), prompt="tree", model=added["id"]), self.cancel, lambda _: None)
        self.assertEqual(picture["source"], record["source"])
        with patch.object(setup, "inference", side_effect=AssertionError("Must refuse before inference")):
            (folder / "notices/README.md").write_bytes(b"changed")
            self.assertEqual(models.entry(self.app, self.root, record, True)["status"], "needs_verification")

    def test_authentication_license_and_missing_pins_refuse_before_header_or_full_download(self):
        for field, value in [("gated", True), ("sha", "main"), ("cardData", {})]:
            data = copy.deepcopy(self.data); data[field] = value
            with self.subTest(field=field), patch.object(catalog, "fetch_json", return_value=data), patch.object(hub, "open_file", side_effect=AssertionError("No file requests")):
                payload = dict(self.payload, revision="")
                self.assertFalse(hub.plan(self.app, payload, self.cancel)["canInstall"])

    def test_no_gpu_low_space_and_unready_base_are_plain_preflight_refusals(self):
        with patch.object(setup, "check_hardware", side_effect=setup.SetupError("No NVIDIA graphics card was found.")):
            self.assertIn("No NVIDIA", hub.plan(self.app, self.payload, self.cancel)["message"])
        small = machine("GTX 1660 Ti", 6144, "7.5"); small["drive"]["freeBytes"] = 10
        with patch("hardware.detect", return_value=small), patch.object(hub, "open_file", side_effect=AssertionError("No file requests")):
            self.assertIn("Free space", hub.plan(self.app, self.payload, self.cancel)["message"])
        setup.state_path(self.root).unlink()
        self.assertIn("Set up Stable Diffusion", hub.plan(self.app, self.payload, self.cancel)["message"])

    def test_unsupported_headers_are_refused_before_the_large_download(self):
        tensors = dict(CHECKPOINT)
        tensors["conditioner.embedders.0.fake"] = ("F16", [1])
        self.file = checkpoint(self.folder / "sdxl.safetensors", changes=tensors)
        self.data["siblings"][0]["size"] = self.file.stat().st_size
        result = hub.plan(self.app, dict(self.payload, revision=""), self.cancel)
        self.assertFalse(result["canInstall"])
        self.assertIn("XL", result["message"])
        self.assertTrue(all(item[3] is not None for item in self.requests if isinstance(item, tuple)))

    def test_sdxl_files_need_the_sdxl_setup_and_are_recorded_as_sdxl(self):
        self.file = write_safetensors(self.folder / "xl.safetensors", XL_CHECKPOINT)
        self.sha = setup.digest(self.file)
        self.data["siblings"][0].update(size=self.file.stat().st_size, lfs=dict(sha256=self.sha))
        self.payload.update(sha256=self.sha, bytes=self.file.stat().st_size)
        planned = hub.plan(self.app, self.payload, self.cancel)
        self.assertEqual((planned["canInstall"], planned["architecture"]), (False, "sdxl"))
        self.assertIn("Set up Stable Diffusion XL", planned["message"])
        with patch.object(models, "xl_ready", return_value=dict(runtime="xl-fp32-sequential")):
            planned = hub.plan(self.app, self.payload, self.cancel)
            self.assertTrue(planned["canInstall"])
            self.assertIn("1024 × 1024", planned["message"])
            added = self.install()
        self.assertEqual((added["architecture"], self.tests[-1][0], self.tests[-1][1]["family"]), ("sdxl", "verify-xl", "sdxl"))

    def test_reviewed_plan_must_match_exact_revision_file_size_and_hash(self):
        for name, value in [("licenseReviewed", False), ("licenseReviewed", "true"), ("revision", "main"),
                            ("sha256", "b" * 64), ("bytes", True), ("file", "../escape.safetensors")]:
            with self.subTest(name=name):
                before = len(self.tests)
                with self.assertRaises((ValueError, catalog.CatalogError)):
                    hub.install(self.app, dict(self.payload, **{name: value}), self.cancel, lambda _: None)
                self.assertEqual(len(self.tests), before)
                self.assertEqual(self.imported(), [])
        changed = copy.deepcopy(self.data); changed["sha"] = "b" * 40
        with patch.object(catalog, "fetch_json", return_value=changed), self.assertRaisesRegex(catalog.CatalogError, "requested model revision"):
            self.install()

    def test_failed_gpu_verification_cancellation_and_corruption_remove_the_unready_folder(self):
        with patch.object(setup, "inference", side_effect=setup.SetupError("GPU test failed")), self.assertRaisesRegex(setup.SetupError, "GPU test failed"):
            self.install()
        self.assertEqual(self.imported(), [])

        def cancel_download(*args, **kwargs):
            self.cancel.set()
            return self.open(*args, **kwargs)
        with patch.object(hub, "open_file", side_effect=cancel_download), self.assertRaises(setup.Cancelled):
            self.install()
        self.cancel.clear()
        self.assertEqual(self.imported(), [])
        # Correct header but wrong whole-file digest must fail before GPU inference.
        data = copy.deepcopy(self.data); data["siblings"][0]["lfs"]["sha256"] = "b" * 64
        with patch.object(catalog, "fetch_json", return_value=data), self.assertRaisesRegex(setup.SetupError, "checksum"):
            hub.install(self.app, dict(self.payload, sha256="b" * 64), self.cancel, lambda _: None)
        self.assertEqual(self.imported(), [])

    def test_cancellation_after_writes_and_space_lost_during_download_clean_partial_files(self):
        def stop(progress):
            if progress.get("completed", 0): self.cancel.set()
        with self.assertRaises(setup.Cancelled):
            hub.install(self.app, self.payload, self.cancel, stop)
        self.cancel.clear()
        self.assertEqual(self.imported(), [])
        with patch.object(hub.storage, "drive_info", side_effect=[dict(freeBytes=10 * 1024**3), dict(freeBytes=0)]), \
                self.assertRaisesRegex(setup.SetupError, "running out of space"):
            self.install()
        self.assertEqual(self.imported(), [])
        self.assertEqual(self.tests, [])

    def test_notice_checksum_failure_and_remote_configuration_never_reach_inference(self):
        data = copy.deepcopy(self.data)
        data["siblings"].append(dict(rfilename="config.py", size=100, blobId="a" * 40))
        data["siblings"][1]["blobId"] = "b" * 40
        with patch.object(catalog, "fetch_json", return_value=data), self.assertRaisesRegex(setup.SetupError, "checksum"):
            self.install()
        self.assertEqual(self.tests, [])
        self.assertEqual(self.imported(), [])
        self.assertFalse(any(item[2] == "config.py" for item in self.requests if isinstance(item, tuple)))

    def test_crash_recovery_and_duplicate_refusal_preserve_ready_models(self):
        added = self.install()
        before = len([item for item in self.requests if isinstance(item, tuple) and item[3] is None])
        with self.assertRaisesRegex(models.Refused, "already added"):
            self.install()
        after = len([item for item in self.requests if isinstance(item, tuple) and item[3] is None])
        self.assertEqual(before, after)
        orphan = models.folder_of(self.root, "c" * 32); orphan.mkdir()
        setup.atomic_json(orphan / "model.json", dict(status="copying"))
        (orphan / "model.safetensors.partial").write_bytes(b"unfinished")
        with setup.exclusive(self.root): setup.recover_interrupted(self.root)
        self.assertFalse(orphan.exists())
        self.assertTrue(models.folder_of(self.root, added["id"]).exists())

    def test_native_planner_sizing_does_not_read_input_and_rejects_unsafe_files(self):
        core = hub.storage.library(ROOT, "mw_plan_hub_model")
        function = core.mw_plan_hub_model
        function.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
        function.restype = ctypes.c_int32
        needed = ctypes.c_size_t()
        self.assertEqual(function(None, 0, None, 0, ctypes.byref(needed)), 2)
        self.assertEqual(needed.value, 65536)
        output = ctypes.create_string_buffer(needed.value)
        self.assertEqual(function(None, 0, output, len(output), ctypes.byref(needed)), 1)


class DownloadTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(); self.addCleanup(temp.cleanup)
        self.root = Path(temp.name).resolve()
        self.target = self.root / "model.safetensors"
        self.body = b"model bytes"
        self.entry = dict(path="model.safetensors", bytes=len(self.body), digest=hashlib.sha256(self.body).hexdigest())

    def test_available_network_bytes_report_progress_and_allow_cancellation_before_a_full_chunk(self):
        response = Response(self.body)
        read = response.read
        response.read1 = lambda count: read(min(count, 2))
        response.read = lambda count: (_ for _ in ()).throw(AssertionError("Do not wait to fill the large read"))
        cancel, updates = threading.Event(), []
        def progress(value):
            updates.append(value["completed"])
            cancel.set()
        with patch.object(hub, "open_file", return_value=response), self.assertRaises(setup.Cancelled):
            hub.download(ROOT, self.root, "owner/model", "a" * 40, self.entry, self.target, cancel, progress)
        self.assertEqual(updates, [2])
        self.assertEqual(list(self.root.iterdir()), [])

    def test_truncated_oversized_wrong_length_and_cancelled_downloads_leave_nothing(self):
        for body in (self.body[:-1], self.body + b"extra"):
            with patch.object(hub, "open_file", return_value=Response(body)), self.assertRaises(setup.SetupError):
                hub.download(ROOT, self.root, "owner/model", "a" * 40, self.entry, self.target, threading.Event(), lambda _: None)
            self.assertEqual(list(self.root.iterdir()), [])
        cancel = threading.Event(); cancel.set()
        with patch.object(hub, "open_file", return_value=Response(self.body)), self.assertRaises(setup.Cancelled):
            hub.download(ROOT, self.root, "owner/model", "a" * 40, self.entry, self.target, cancel, lambda _: None)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_redirects_refuse_http_and_unrelated_hosts(self):
        handler = hub.HttpsRedirect()
        request = hub.urllib.request.Request("https://huggingface.co/owner/model/resolve/" + "a" * 40 + "/model.safetensors")
        for url in ("http://huggingface.co/file", "https://example.org/file", "https://huggingface.co.evil.example/file", "https://user:@cdn.hf.co/file"):
            with self.subTest(url=url), self.assertRaises(setup.SetupError):
                handler.redirect_request(request, None, 302, "Found", {}, url)

    def test_wrong_range_and_oversized_headers_are_rejected(self):
        response = Response(b"1234567890", 0, 7)
        response.headers["Content-Range"] = "bytes 0-7/99"
        with patch.object(hub, "open_file", return_value=response), self.assertRaisesRegex(setup.SetupError, "range"):
            hub.read_range("owner/model", "a" * 40, dict(path="model.safetensors", bytes=10), 0, 7, threading.Event())
        with patch.object(hub, "read_range", return_value=struct.pack("<Q", hub.HEADER_LIMIT + 1)), self.assertRaises(models.Refused):
            hub.identify_remote("owner/model", "a" * 40, dict(path="model.safetensors", bytes=100_000_000), threading.Event())

    def test_tensor_counts_dimensions_and_payload_ranges_are_bounded_before_expensive_arithmetic(self):
        header = {"weight": dict(dtype="F16", shape=[2**31] * 8, data_offsets=[0, 8])}
        raw = json.dumps(header).encode()
        with self.assertRaises(models.Refused): models.parse_header(raw, len(raw) + 16)
        header["weight"]["shape"] = [1] * 9
        raw = json.dumps(header).encode()
        with self.assertRaises(models.Refused): models.parse_header(raw, len(raw) + 16)
        header = {"first": dict(dtype="F16", shape=[2], data_offsets=[0, 4]),
                  "second": dict(dtype="F16", shape=[2], data_offsets=[0, 4])}
        raw = json.dumps(header).encode()
        with self.assertRaisesRegex(models.Refused, "overlapping"): models.parse_header(raw, len(raw) + 12)
        with patch.object(models, "HEADER_MAX_TENSORS", 1), self.assertRaises(models.Refused):
            models.parse_header(raw, len(raw) + 12)


if __name__ == "__main__": unittest.main()
