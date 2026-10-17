"""Storage summary and removal with temporary owned data; standard library only."""
import json
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

FILES = {
    f"runtimes/{setup.PROFILE}/python/python.exe": 10,
    f"models/{setup.RECIPE}/unet/weights.bin": 100,
    f"models/{setup.UPSCALER}/RealESRGAN_x4plus.pth": 6,
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


def sizes(result):
    return {item["id"]: item["bytes"] for item in result["items"]}


class StorageTests(unittest.TestCase):
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
