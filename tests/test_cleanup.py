"""Automatic clean-up and free-space rules with temporary owned data; standard library only.

Each rule is checked for what it removes and, just as much, for what it must keep.
"""
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
# GitHub's runner reports its temp folder by an 8.3 short name; the app resolves data roots to long names.
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import inference_setup as setup
import storage
from test_inference_setup import fake_app, fake_generation, ready_install

DAY = 24 * 3600


def job(number):
    return f"{number:032x}"


def write(root, relative, body=b"x", age=0):
    path = Path(root) / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(body)
    stamp = time.time() - age
    os.utime(path, (stamp, stamp))
    return path


def record(root, job_id, status, age, **fields):
    path = Path(root) / "jobs" / f"{job_id}.json"
    setup.atomic_json(path, dict(schemaVersion=1, id=job_id, status=status, startedUtc="2026-10-01T00:00:00+00:00", **fields))
    stamp = time.time() - age
    os.utime(path, (stamp, stamp))


def listed_problems(folder):
    return setup.library({"dataRoot": str(folder)})["problems"]


class CleanupTests(unittest.TestCase):
    def test_policy_keeps_exactly_the_unfinished_jobs_library_lists(self):
        with tempfile.TemporaryDirectory() as folder:
            report = storage.tidy(ROOT, Path(folder), False, False)
            self.assertEqual(report["policy"]["problemJobs"], setup.LIBRARY_PROBLEMS)
            self.assertEqual(report["removedFiles"], 0)

    def test_old_unfinished_jobs_go_but_listed_ones_keep_their_retry_pictures(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for number in range(1, 56):  # Larger numbers are newer.
                job_id = job(number)
                write(root, f"artifacts/{job_id}/source.png", b"picture")
                write(root, f"logs/generate-{job_id}.log", b"log")
                record(root, job_id, "failed", 10_000 - number, kind="generate", message="CUDA out of memory", log=f"generate-{job_id}.log",
                       settings={"prompt": f"try {number}", "sourceImage": f"{job_id}/source.png", "strength": 0.6})
            done = job(99)
            write(root, f"artifacts/{done}/image.png", b"png")
            write(root, f"artifacts/{done}/source.png", b"picture")
            record(root, done, "succeeded", 90 * DAY, kind="generate", imagePath=f"{done}/image.png",
                   settings={"prompt": "kept", "sourceImage": f"{done}/source.png", "strength": 0.6})
            before = [problem["id"] for problem in listed_problems(root)]

            report = storage.tidy(ROOT, root, False, False)

            problems = listed_problems(root)
            self.assertEqual([problem["id"] for problem in problems], before)
            self.assertEqual(len(problems), setup.LIBRARY_PROBLEMS)
            self.assertEqual(report["jobRecords"], 5)
            for problem in problems:  # Retry still restores each listed job's private starting picture and log.
                self.assertTrue(Path(problem["settings"]["sourceImage"]).is_file())
                self.assertTrue(Path(problem["logPath"]).is_file())
            for number in range(1, 6):
                self.assertFalse((root / "jobs" / f"{job(number)}.json").exists())
                self.assertFalse((root / "artifacts" / job(number)).exists())
                self.assertFalse((root / "logs" / f"generate-{job(number)}.log").exists())
            images = setup.library({"dataRoot": folder})["artifacts"]
            self.assertEqual([image["id"] for image in images], [done])
            self.assertTrue(Path(images[0]["settings"]["sourceImage"]).is_file(), "Use settings keeps its picture")

    def test_logs_are_bounded_by_count_and_age_but_unfinished_jobs_keep_theirs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for number in range(1, 311):
                write(root, f"logs/generate-{job(number)}.log", age=1000 - number)
            stuck = job(500)
            record(root, stuck, "interrupted", 60 * DAY, kind="upscale")
            write(root, f"logs/upscale-{stuck}.log", age=60 * DAY)
            write(root, f"logs/verify-{job(501)}.log", age=31 * DAY)
            write(root, "logs/my notes.txt", age=400 * DAY)

            report = storage.tidy(ROOT, root, False, False)

            self.assertEqual(report["logs"], 11)
            self.assertEqual(report["policy"]["logs"], 300)
            self.assertEqual(report["policy"]["logDays"], 30)
            kept = sorted(path.name for path in (root / "logs").iterdir())
            self.assertEqual(len(kept), 302)
            self.assertIn(f"upscale-{stuck}.log", kept)
            self.assertIn("my notes.txt", kept)
            self.assertNotIn(f"verify-{job(501)}.log", kept)
            self.assertNotIn(f"generate-{job(10)}.log", kept)
            self.assertIn(f"generate-{job(11)}.log", kept)

    def test_crash_leftovers_go_and_work_and_user_files_stay(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            write(root, "bootstrap/python.zip.partial", age=4 * DAY)
            write(root, "bootstrap/uv.zip.partial", age=60)
            hub = f"models/{setup.RECIPE}/.cache/huggingface/download/unet"
            write(root, f"{hub}/weights.incomplete", age=5 * DAY)
            write(root, f"models/{setup.RECIPE}/unet/weights.safetensors", age=90 * DAY)
            write(root, ".removing/cache-123/half-removed.whl")
            stopped = job(7)
            record(root, stopped, "interrupted", DAY, kind="generate", settings={"prompt": "p"})
            write(root, f"artifacts/{stopped}/image.partial")
            write(root, f"artifacts/{job(8)}/image.png", age=400 * DAY)
            write(root, "my notes.txt", age=400 * DAY)
            write(root, "exports/old.partial", age=400 * DAY)

            report = storage.tidy(ROOT, root, False, False)

            self.assertEqual(report["partialDownloads"], 2)
            for gone in ["bootstrap/python.zip.partial", f"{hub}/weights.incomplete", ".removing",
                         f"artifacts/{stopped}/image.partial"]:
                self.assertFalse((root / gone).exists(), gone)
            for kept in ["bootstrap/uv.zip.partial", f"models/{setup.RECIPE}/unet/weights.safetensors",
                         f"jobs/{stopped}.json", f"artifacts/{job(8)}/image.png", "my notes.txt", "exports/old.partial"]:
                self.assertTrue((root / kept).is_file(), kept)

    def test_verified_setup_removes_its_downloads_unless_kept(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for relative in ["cache/uv/torch.whl", "cache/huggingface/xet/chunk", "bootstrap/python.zip",
                             f"runtimes/{setup.PROFILE}/python/python.exe", f"models/{setup.RECIPE}/model_index.json",
                             f"artifacts/{job(1)}/image.png"]:
                write(root, relative, b"x" * 100)
            write(root, f"models/{setup.UPSCALER}/weights.pth.partial", age=60)
            noop = lambda _: None
            result = storage.after_setup(ROOT, {"dataRoot": folder}, {"status": "failed"}, threading.Event(), noop)
            self.assertNotIn("cleanedBytes", result)
            self.assertTrue((root / "cache/uv/torch.whl").is_file())

            result = storage.after_setup(ROOT, {"dataRoot": folder, "keepDownloadCaches": True}, {"status": "ready"},
                                         threading.Event(), noop)
            self.assertEqual(result["cleanedBytes"], 1)  # Only the stale partial download.
            self.assertTrue((root / "cache/uv/torch.whl").is_file())
            self.assertTrue((root / "bootstrap/python.zip").is_file())

            result = storage.after_setup(ROOT, {"dataRoot": folder}, {"status": "ready", "message": "Ready"},
                                         threading.Event(), noop)
            self.assertEqual(result["cleanedBytes"], 300)
            self.assertEqual(result["message"], "Ready")
            self.assertFalse((root / "cache").exists() or (root / "bootstrap").exists())
            for kept in [f"runtimes/{setup.PROFILE}/python/python.exe", f"models/{setup.RECIPE}/model_index.json",
                         f"artifacts/{job(1)}/image.png"]:
                self.assertTrue((root / kept).is_file(), kept)
            self.assertEqual(storage.summary({"dataRoot": folder})["installationBytes"], 200)

    def test_caches_stay_until_setup_is_verified(self):
        with tempfile.TemporaryDirectory() as folder:
            write(Path(folder), "cache/uv/torch.whl")
            report = storage.tidy(ROOT, Path(folder), False, True)  # The adapter never asks without verification.
            self.assertFalse(report["downloadCaches"])
            self.assertTrue((Path(folder) / "cache/uv/torch.whl").is_file())

    def test_clean_up_waits_for_the_data_folder_owner(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            write(root, "bootstrap/python.zip.partial", age=9 * DAY)
            write(root, "cache/uv/torch.whl")
            with setup.exclusive(root):
                with self.assertRaisesRegex(setup.SetupError, "Another operation"):
                    storage.tidy(ROOT, root, True, True)
                result = storage.after_setup(ROOT, {"dataRoot": folder}, {"status": "ready"}, threading.Event(), lambda _: None)
                self.assertEqual(result["cleanedBytes"], 0)  # Setup still reports ready; the next start tries again.
            self.assertTrue((root / "bootstrap/python.zip.partial").is_file())
            self.assertTrue((root / "cache/uv/torch.whl").is_file())
            cancel = threading.Event()
            cancel.set()
            with self.assertRaises(setup.Cancelled):
                storage.tidy(ROOT, root, True, True, cancel)
            self.assertTrue((root / "cache/uv/torch.whl").is_file())

    @unittest.skipUnless(os.name == "nt", "Windows junction containment")
    def test_junctions_are_never_followed(self):
        import _winapi
        with tempfile.TemporaryDirectory() as folder, tempfile.TemporaryDirectory() as outside:
            root, external = Path(folder), Path(outside)
            write(external, "private.log", age=400 * DAY)
            write(external, "private.partial", age=400 * DAY)
            write(root, "cache/uv/torch.whl")
            _winapi.CreateJunction(str(external), str(root / "cache/linked"))
            (root / "models").mkdir()
            _winapi.CreateJunction(str(external), str(root / f"models/{setup.RECIPE}"))
            with self.assertRaisesRegex(ValueError, "link or junction"):
                storage.tidy(ROOT, root, True, True)
            os.rmdir(root / f"models/{setup.RECIPE}")  # Removes the junction itself.
            storage.tidy(ROOT, root, True, True)
            self.assertFalse((root / "cache").exists())
            self.assertTrue((external / "private.log").is_file() and (external / "private.partial").is_file())
            _winapi.CreateJunction(str(external), str(root / "logs"))
            with self.assertRaisesRegex(ValueError, "link or junction"):
                storage.tidy(ROOT, root, False, False)
            self.assertTrue((external / "private.log").is_file())


class FreeSpaceTests(unittest.TestCase):
    def low_space(self, free):
        return patch.object(setup.shutil, "disk_usage", return_value=type("Usage", (), {"free": free})())

    def assert_nothing_written(self, root):
        self.assertEqual(sorted(path.name for path in (root / "jobs").iterdir()), ["install-sd15.json"])
        self.assertFalse((root / "artifacts").exists())
        self.assertFalse((root / "logs").exists())

    def test_generation_refuses_low_space_before_any_write(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            with self.low_space(100 * 1024**2), patch.object(setup, "inference", side_effect=AssertionError("ran")):
                with self.assertRaisesRegex(setup.SetupError, "not enough free space .* move the data folder in Settings"):
                    setup.generate(app, {"dataRoot": str(root), "prompt": "house"}, threading.Event(), lambda _: None)
            self.assert_nothing_written(root)

    def test_upscale_refuses_low_space_before_copying_the_picture(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            picture = write(folder, "picture.png", b"p" * 1000)
            needed = setup.space_needed("upscale", {"sourceImage": str(picture), "scale": 4})
            payload = {"dataRoot": str(root), "sourceImage": str(picture), "scale": 4}
            with self.low_space(needed - 1), patch.object(setup, "inference", side_effect=AssertionError("ran")):
                with self.assertRaisesRegex(setup.SetupError, "not enough free space"):
                    setup.upscale(app, payload, threading.Event(), lambda _: None)
            self.assert_nothing_written(root)
            with self.low_space(needed), patch.object(setup, "inference", side_effect=fake_generation):
                self.assertEqual(setup.upscale(app, payload, threading.Event(), lambda _: None)["status"], "succeeded")

    def test_room_needed_covers_the_largest_picture_each_job_can_save(self):
        with tempfile.TemporaryDirectory() as folder:
            picture = write(folder, "picture.png", b"p" * 1000)
            mask = write(folder, "mask.png", b"m" * 10)
            headroom = setup.WRITE_HEADROOM_BYTES
            self.assertEqual(setup.space_needed("generate", {}), 768 * 768 * 3 + headroom)
            self.assertEqual(setup.space_needed("generate", {"sourceImage": str(picture)}), 1000 + 768 * 768 * 3 + headroom)
            self.assertEqual(setup.space_needed("generate", {"sourceImage": str(picture), "maskImage": str(mask)}),
                             1010 + setup.EDIT_MAX_SIDE ** 2 * 3 + headroom)
            self.assertEqual(setup.space_needed("upscale", {"sourceImage": str(picture), "scale": 2}),
                             1000 + (setup.UPSCALE_MAX_SIDE * 2) ** 2 * 3 + headroom)
            self.assertEqual(setup.size_text(5 * 1024**3), "5.0 GB")
            self.assertEqual(setup.size_text(100 * 1024**2), "100 MB")


if __name__ == "__main__":
    unittest.main()
