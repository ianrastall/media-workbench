"""Setup invariants with temporary owned data; never downloads ML weights."""
import json
import io
from pathlib import Path
import sys
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_setup as setup


class SetupTests(unittest.TestCase):
    def test_invalid_generation_does_not_create_data(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / "untouched"
            for bad in [{"prompt": ""}, {"prompt": "x", "seed": -1}, {"prompt": "x", "steps": True}]:
                with self.assertRaises(ValueError):
                    setup.generate(ROOT, dict(bad, dataRoot=str(root)), threading.Event(), lambda _: None)
            self.assertFalse(root.exists())

    def test_interrupted_install_is_recovered_without_claiming_ready(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            setup.atomic_json(setup.state_path(root), {"status": "verifying", "message": "Testing"})
            result = setup.status(ROOT, {"dataRoot": str(root)})
            self.assertEqual(result["status"], "interrupted")
            self.assertEqual(json.loads(setup.state_path(root).read_text())["status"], "interrupted")

    def test_changed_recipe_invalidates_ready_state(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            setup.atomic_json(setup.state_path(root), {"status": "ready", "profileStamp": "old"})
            self.assertEqual(setup.status(ROOT, {"dataRoot": folder})["status"], "needs_verification")

    def test_active_install_is_readable_and_not_marked_interrupted(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            setup.atomic_json(setup.state_path(root), {"status": "runtime", "message": "Installing"})
            with setup.exclusive(root):
                self.assertEqual(setup.status(ROOT, {"dataRoot": folder})["status"], "runtime")
            self.assertEqual(setup.status(ROOT, {"dataRoot": folder})["status"], "interrupted")

    def test_failed_inference_never_commits_ready_state(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with patch.object(setup.shutil, "disk_usage", return_value=type("Usage", (), {"free": 0})()):
                with self.assertRaises(setup.SetupError):
                    setup.install(ROOT, {"dataRoot": folder}, threading.Event(), lambda _: None)
            self.assertEqual(json.loads(setup.state_path(root).read_text())["status"], "failed")

    def test_generation_requires_verified_installation(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(setup.SetupError):
                setup.generate(ROOT, {"dataRoot": folder, "prompt": "house"}, threading.Event(), lambda _: None)
            self.assertFalse((Path(folder) / "artifacts").exists())

    def test_missing_runtime_revokes_ready_state(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            setup.atomic_json(setup.state_path(root), {"status": "ready", "profileStamp": setup.profile_stamp(ROOT)})
            self.assertEqual(setup.status(ROOT, {"dataRoot": folder})["status"], "needs_verification")

    def test_cancelled_generation_is_durable_and_absent_from_library(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            setup.atomic_json(setup.state_path(root), {"status": "ready", "profileStamp": setup.profile_stamp(ROOT)})
            with patch.object(setup, "verify_files"), patch.object(setup, "inference", side_effect=setup.Cancelled("Cancelled")):
                with self.assertRaises(setup.Cancelled):
                    setup.generate(ROOT, {"dataRoot": folder, "prompt": "house"}, threading.Event(), lambda _: None)
            jobs = [json.loads(p.read_text()) for p in (root / "jobs").glob("*.json") if p.name != "install-sd15.json"]
            self.assertEqual(len(jobs), 1)
            self.assertEqual(jobs[0]["status"], "cancelled")
            self.assertEqual(setup.library({"dataRoot": folder})["artifacts"], [])

    def test_orphaned_generation_is_recorded_interrupted(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            job_id = "a" * 32
            job = root / "jobs" / f"{job_id}.json"
            setup.atomic_json(job, {"id": job_id, "status": "running", "settings": {"prompt": "house"}})
            partial = root / "artifacts" / job_id / "image.partial"
            partial.parent.mkdir(parents=True)
            partial.write_bytes(b"incomplete")
            stale = root / "jobs" / f"{job_id}.json.{'b' * 32}.tmp"
            stale.write_text("{")
            listing = setup.library({"dataRoot": folder})
            self.assertEqual((listing["artifacts"], listing["recovered"]), ([], 1))
            record = json.loads(job.read_text())
            self.assertEqual(record["status"], "interrupted")
            self.assertIn("interruptedUtc", record)
            self.assertFalse(partial.exists())
            self.assertFalse(stale.exists())
            self.assertEqual(setup.library({"dataRoot": folder})["recovered"], 0)

    def test_live_generation_is_not_marked_interrupted(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            job = root / "jobs" / f"{'c' * 32}.json"
            setup.atomic_json(job, {"id": "c" * 32, "status": "running"})
            with setup.exclusive(root):
                self.assertEqual(setup.library({"dataRoot": folder})["recovered"], 0)
                setup.status(ROOT, {"dataRoot": folder})
                self.assertEqual(json.loads(job.read_text())["status"], "running")
            setup.status(ROOT, {"dataRoot": folder})
            self.assertEqual(json.loads(job.read_text())["status"], "interrupted")

    def test_damaged_job_record_does_not_block_library(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "jobs").mkdir()
            (root / "jobs" / "broken.json").write_text("{not json")
            (root / "jobs" / "list.json").write_text("[]")
            self.assertEqual(setup.library({"dataRoot": folder})["artifacts"], [])

    def test_bad_download_hash_is_never_finalized(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "python.zip"
            response = io.BytesIO(b"tampered")
            response.headers = {}
            with patch.object(setup.urllib.request, "urlopen", return_value=response):
                with self.assertRaises(setup.SetupError):
                    setup.download({"url": "https://example.invalid/python.zip", "sha256": "0" * 64}, target, threading.Event(), lambda _: None)
            self.assertFalse(target.exists())

    def test_corrupt_model_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(setup.SetupError):
                setup.verify_files(ROOT, Path(folder), threading.Event(), lambda _: None)

    def test_archive_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with zipfile.ZipFile(root / "bad.zip", "w") as archive:
                archive.writestr("../escape.txt", "bad")
            with self.assertRaises(setup.SetupError):
                setup.extract(root / "bad.zip", root / "extract")
            self.assertFalse((root / "escape.txt").exists())

    def test_cancel_stops_child_and_releases_lock(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            cancel = threading.Event()
            with setup.exclusive(root):
                def on_line(_):
                    cancel.set()
                with self.assertRaises(setup.Cancelled):
                    setup.run_child([sys.executable, "-u", "-c", "import time; print('started', flush=True); time.sleep(30)"], cancel, on_line)
            with setup.exclusive(root):
                pass

    def test_child_does_not_inherit_open_protocol_stdin(self):
        code = (
            "import sys,threading; sys.path.insert(0,sys.argv[1]); import inference_setup as s; "
            "cancel=threading.Event(); timer=threading.Timer(3,cancel.set); timer.start(); "
            "s.run_child([sys.executable,'-u','-c',\"import sys; print('EOF='+repr(sys.stdin.read()),flush=True)\"],cancel,lambda p: print(p['stage'],flush=True)); "
            "timer.cancel()"
        )
        worker = subprocess.Popen([sys.executable, "-u", "-c", code, str(ROOT / "workers/python")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            worker.wait(timeout=6)  # Keep protocol stdin open for the full child lifetime.
            self.assertEqual(worker.returncode, 0, worker.stderr.read().decode())
            self.assertIn(b"EOF=''", worker.stdout.read())
        finally:
            if worker.poll() is None:
                worker.kill()
                worker.wait(timeout=3)
            worker.stdin.close()
            worker.stdout.close()
            worker.stderr.close()


if __name__ == "__main__":
    unittest.main()
