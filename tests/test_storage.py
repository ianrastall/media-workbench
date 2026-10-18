"""Storage summary and removal with temporary owned data; standard library only."""
import ctypes
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
# GitHub's runner reports its temp folder by an 8.3 short name; the app resolves data roots to long names.
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_setup as setup
import storage
import video

FILES = {
    f"runtimes/{setup.PROFILE}/python/python.exe": 10,
    f"models/{setup.RECIPE}/unet/weights.bin": 100,
    f"models/{setup.UPSCALER}/RealESRGAN_x4plus.pth": 6,
    "models/imported/" + "b" * 32 + "/model.safetensors": 7,
    "cache/uv/wheel.whl": 40,
    "cache/huggingface/token-free.json": 2,
    "bootstrap/python.zip": 8,
    "artifacts/" + "a" * 32 + "/image.png": 5,
    "logs/generate-" + "a" * 32 + ".log": 3,
}


def populate(root):
    for name, size in FILES.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"x" * size)
    setup.atomic_json(setup.state_path(root), {"status": "ready"})
    setup.atomic_json(root / "jobs" / ("a" * 32 + ".json"), {"id": "a" * 32, "status": "succeeded"})
    setup.atomic_json(root / "models/imported" / ("b" * 32) / "model.json", {"id": "b" * 32, "status": "ready"})


def sizes(result):
    return {item["id"]: item["bytes"] for item in result["items"]}


class StorageTests(unittest.TestCase):
    def test_inventory_layout_matches_the_supported_installation(self):
        self.assertEqual(json.loads((ROOT / "profiles/storage-layout.json").read_text()),
                         {"runtimeProfileId": setup.PROFILE, "modelIds": [setup.RECIPE, setup.UPSCALER, video.MOTION, setup.XL_MODEL]})

    def test_summary_reports_each_area(self):
        with tempfile.TemporaryDirectory() as folder:
            populate(Path(folder))
            result = storage.summary({"dataRoot": folder})
            measured = sizes(result)
            self.assertEqual({k: measured[k] for k in ["runtime", "model", "caches", "images", "logs"]},
                             {"runtime": 10, "model": 106, "caches": 50, "images": 5, "logs": 3})
            self.assertEqual(result["installationBytes"], 166)
            self.assertGreater(result["freeBytes"], 0)
            clearable = {item["id"] for item in result["items"] if item["clearable"]}
            self.assertEqual(clearable, {"caches", "logs"})
            self.assertEqual({item["id"]: item["files"] for item in result["items"]},
                             {"runtime": 1, "model": 2, "caches": 3, "imported": 2, "images": 1, "logs": 1, "jobs": 2, "library": 0,
                              "temp": 0})
            self.assertEqual(measured["imported"], 7 + (Path(folder) / "models/imported" / ("b" * 32) / "model.json").stat().st_size)

    def test_unicode_roots_and_only_managed_profiles_are_measured(self):
        with tempfile.TemporaryDirectory(prefix="MediaWorkbench-café-森林-") as folder:
            root = Path(folder)
            populate(root)
            for relative in ["models/unknown/weights.bin", "runtimes/unknown/tool.bin", "other/file.bin"]:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x" * 1000)
            self.assertEqual(storage.summary({"dataRoot": folder})["installationBytes"], 166)

    def test_cancellation_during_inventory_preserves_files(self):
        class CancelAfterEntries:
            calls = 0

            def is_set(self):
                self.calls += 1
                return self.calls >= 10

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            populate(root)
            cancel = CancelAfterEntries()
            with self.assertRaises(setup.Cancelled):
                storage.summary({"dataRoot": folder}, cancel)
            self.assertGreaterEqual(cancel.calls, 10)
            self.assertTrue((root / "cache/uv/wheel.whl").is_file())
            self.assertEqual(storage.summary({"dataRoot": folder})["installationBytes"], 166)

    def test_native_buffer_queries_and_precancellation_do_not_create_folders(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "must-not-exist"
            core = ctypes.CDLL(str(setup.native_core_path(ROOT)))
            callback_type = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)
            callback = callback_type(lambda _: 1)
            function = core.mw_storage_summary
            function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, callback_type, ctypes.c_void_p,
                                 ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
            function.restype = ctypes.c_int32
            request = json.dumps(dict(dataRoot=str(target), runtimeProfileId=setup.PROFILE,
                                      modelIds=[setup.RECIPE, setup.UPSCALER])).encode()
            required = ctypes.c_size_t()
            self.assertEqual(function(request, len(request), callback, None, None, 0, ctypes.byref(required)), 2)
            self.assertEqual(required.value, 8192)
            output = ctypes.create_string_buffer(required.value)
            self.assertEqual(function(request, len(request), callback, None, output, len(output), ctypes.byref(required)), 5)
            self.assertFalse(target.exists())

    def test_invalid_storage_roots_create_nothing(self):
        for root in ["relative", str(Path(ROOT.anchor)), None, ["not", "a", "path"]]:
            with self.subTest(root=root), self.assertRaises(ValueError):
                storage.summary({"dataRoot": root})

    @unittest.skipUnless(os.name == "nt", "Windows junction containment")
    def test_internal_junctions_are_skipped_and_managed_area_junctions_are_refused(self):
        import _winapi
        with tempfile.TemporaryDirectory() as folder, tempfile.TemporaryDirectory() as outside:
            root = Path(folder)
            populate(root)
            external = Path(outside)
            (external / "private.bin").write_bytes(b"x" * 1000)
            _winapi.CreateJunction(str(external), str(root / "cache/linked-folder"))
            self.assertEqual(sizes(storage.summary({"dataRoot": folder}))["caches"], 50)
            _winapi.CreateJunction(str(external), str(root / "library"))
            with self.assertRaisesRegex(ValueError, "link or junction"):
                storage.summary({"dataRoot": folder})
            self.assertEqual((external / "private.bin").stat().st_size, 1000)

    def test_clearing_caches_keeps_installation_and_work(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            populate(root)
            result = storage.clear({"dataRoot": folder, "area": "caches"}, threading.Event(), lambda _: None)
            self.assertEqual(sizes(result)["caches"], 0)
            self.assertFalse((root / "cache").exists() or (root / "bootstrap").exists())
            for kept in [f"runtimes/{setup.PROFILE}/python/python.exe", f"models/{setup.RECIPE}/unet/weights.bin",
                         "artifacts/" + "a" * 32 + "/image.png"]:
                self.assertTrue((root / kept).is_file(), kept)
            self.assertEqual(json.loads(setup.state_path(root).read_text())["status"], "ready")

    def test_removing_installation_keeps_images_and_records(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            populate(root)
            result = storage.clear({"dataRoot": folder, "area": "installation"}, threading.Event(), lambda _: None)
            self.assertEqual(result["installationBytes"], 0)
            self.assertFalse(setup.state_path(root).exists())
            self.assertEqual(setup.status(ROOT, {"dataRoot": folder})["status"], "not_installed")
            self.assertTrue((root / "artifacts" / ("a" * 32) / "image.png").is_file())
            self.assertTrue((root / "jobs" / ("a" * 32 + ".json")).is_file())
            # Added models are the user's own; they wait for Stable Diffusion 1.5 to be set up again.
            self.assertTrue((root / "models/imported" / ("b" * 32) / "model.safetensors").is_file())

    def test_work_and_unknown_areas_cannot_be_removed(self):
        with tempfile.TemporaryDirectory() as folder:
            populate(Path(folder))
            for area in ["images", "jobs", "runtime", "../escape", None]:
                with self.assertRaises(ValueError, msg=area):
                    storage.clear({"dataRoot": folder, "area": area}, threading.Event(), lambda _: None)
            self.assertEqual(sizes(storage.summary({"dataRoot": folder}))["images"], 5)

    def test_removal_waits_for_the_data_folder_owner(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            populate(root)
            with setup.exclusive(root):
                with self.assertRaises(setup.SetupError):
                    storage.clear({"dataRoot": folder, "area": "caches"}, threading.Event(), lambda _: None)
            self.assertTrue((root / "cache/uv/wheel.whl").is_file())


if __name__ == "__main__":
    unittest.main()
