"""Setup invariants with temporary owned data; never downloads ML weights."""
import hashlib
import json
import io
import os
from pathlib import Path
import shutil
import sys
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
# GitHub's runner reports its temp folder by an 8.3 short name; the app resolves data roots to long names.
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_setup as setup

MODEL_FILES = [("model_index.json", b"{}"), ("unet/weights.bin", b"weights")]
UPSCALER_BYTES = b"esrgan weights"


def fake_app(folder):
    """Copy the stamped profile files and swap in a tiny two-file model manifest."""
    app = Path(folder) / "app"
    for name in setup.STAMPED_FILES:
        (app / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / name, app / name)
    manifest = json.loads((app / "profiles/sd15-files.json").read_text())
    manifest["files"] = [{"path": path, "size": len(body), "sha256": hashlib.sha256(body).hexdigest()}
                         for path, body in MODEL_FILES]
    (app / "profiles/sd15-files.json").write_text(json.dumps(manifest))
    upscaler = json.loads((app / "profiles/upscaler-files.json").read_text())
    upscaler["files"] = [dict(upscaler["files"][0], size=len(UPSCALER_BYTES), sha256=hashlib.sha256(UPSCALER_BYTES).hexdigest())]
    (app / "profiles/upscaler-files.json").write_text(json.dumps(upscaler))
    return app


def ready_install(app, root):
    for path, body in MODEL_FILES:
        target = root / "models" / setup.RECIPE / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(body)
    weights = root / "models" / setup.UPSCALER / "RealESRGAN_x4plus.pth"
    weights.parent.mkdir(parents=True, exist_ok=True)
    weights.write_bytes(UPSCALER_BYTES)
    stamps = setup.verify_files(app, root, threading.Event(), lambda _: None)
    runtime = root / "runtimes" / setup.PROFILE / "python" / "python.exe"
    runtime.parent.mkdir(parents=True, exist_ok=True)
    runtime.write_bytes(b"")
    setup.atomic_json(setup.state_path(root), {"status": "ready", "profileStamp": setup.profile_stamp(app), "files": stamps})


def fake_generation(app, data, operation, payload, cancel, progress, log_id=None):
    image = data / "artifacts" / payload["jobId"] / "image.png"
    image.parent.mkdir(parents=True, exist_ok=True)
    image.write_bytes(b"png")
    return {"imagePath": payload["jobId"] + "/image.png", "imageSha256": "0" * 64, "seconds": 1.0, "gpu": "Test GPU"}


class SetupTests(unittest.TestCase):
    def test_empty_lock_excludes_a_second_owner_without_a_sentinel_write(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with setup.exclusive(root):
                self.assertEqual((root / ".operation.lock").stat().st_size, 0)
                with self.assertRaises(setup.SetupError):
                    with setup.exclusive(root):
                        self.fail("A second owner acquired the lock")
            with setup.exclusive(root):
                self.assertEqual((root / ".operation.lock").stat().st_size, 0)

    def test_invalid_generation_does_not_create_data(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / "untouched"
            for bad in [{"prompt": ""}, {"prompt": "x", "seed": -1}, {"prompt": "x", "steps": True}]:
                with self.assertRaises(ValueError):
                    setup.generate(ROOT, dict(bad, dataRoot=str(root)), threading.Event(), lambda _: None)
            self.assertFalse(root.exists())

    def test_generation_controls_are_validated(self):
        defaults = setup.validate_generation({"prompt": "house"})
        self.assertEqual((defaults["width"], defaults["height"], defaults["guidanceScale"], defaults["sampler"]), (512, 512, 7.5, "pndm"))
        chosen = setup.validate_generation({"prompt": "house", "width": 768, "height": 512, "guidanceScale": 4, "sampler": "dpmpp-2m"})
        self.assertEqual((chosen["width"], chosen["height"], chosen["guidanceScale"], chosen["sampler"]), (768, 512, 4.0, "dpmpp-2m"))
        for bad in [{"guidanceScale": 0.5}, {"guidanceScale": 21}, {"guidanceScale": True}, {"guidanceScale": "7"},
                    {"width": 640, "height": 640}, {"width": 768, "height": 768}, {"width": 512.0}, {"sampler": "ddim"}, {"sampler": None}]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                setup.validate_generation(dict(bad, prompt="house"))

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
            with patch.object(setup, "files_unchanged", return_value=True), patch.object(setup, "inference", side_effect=setup.Cancelled("Cancelled")):
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


class ReadinessTests(unittest.TestCase):
    def test_profile_stamp_ignores_adapter_source_and_line_endings(self):
        with tempfile.TemporaryDirectory() as folder:
            app = fake_app(folder)
            stamp = setup.profile_stamp(app)
            worker = app / "workers/python/inference_worker.py"
            worker.parent.mkdir(parents=True)
            worker.write_text("# edited adapter source\n")
            lock = app / "profiles/sd15-cu126.lock"
            lock.write_bytes(lock.read_bytes().replace(b"\n", b"\r\n"))
            self.assertEqual(setup.profile_stamp(app), stamp)

    def test_profile_stamp_tracks_adapter_version_and_pinned_files(self):
        with tempfile.TemporaryDirectory() as folder:
            app = fake_app(folder)
            stamp = setup.profile_stamp(app)
            with patch.object(setup, "ADAPTER_VERSION", setup.ADAPTER_VERSION + 1):
                self.assertNotEqual(setup.profile_stamp(app), stamp)
            with (app / "profiles/sd15-cu126.lock").open("a") as lock:
                lock.write("# changed pin\n")
            self.assertNotEqual(setup.profile_stamp(app), stamp)

    def test_generation_checks_recorded_stamps_without_rehashing(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            with patch.object(setup, "digest", side_effect=AssertionError("full hash on generate")), \
                 patch.object(setup, "inference", side_effect=fake_generation):
                record = setup.generate(app, {"dataRoot": str(root), "prompt": "house"}, threading.Event(), lambda _: None)
            self.assertEqual(record["status"], "succeeded")
            self.assertEqual(Path(record["imagePath"]), (root / "artifacts" / record["id"] / "image.png").resolve())
            stored = json.loads((root / "jobs" / f"{record['id']}.json").read_text())
            self.assertEqual(stored["imagePath"], record["id"] + "/image.png")
            self.assertEqual(stored["log"], f"generate-{record['id']}.log")
            self.assertEqual(setup.status(app, {"dataRoot": str(root)})["status"], "ready")

    def test_image_to_image_works_from_a_private_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            original = Path(folder) / "My Pictures" / "holiday photo.JPG"
            original.parent.mkdir()
            original.write_bytes(b"jpeg bytes")
            seen = {}
            def child(app_, data, operation, payload, cancel, progress, log_id=None):
                seen.update(payload)
                self.assertEqual(Path(payload["sourceImage"]).read_bytes(), b"jpeg bytes")
                result = fake_generation(app_, data, operation, payload, cancel, progress)
                return dict(result, width=768, height=512)
            with patch.object(setup, "inference", side_effect=child):
                record = setup.generate(app, {"dataRoot": str(root), "prompt": "watercolor", "sourceImage": str(original), "strength": 0.35},
                                        threading.Event(), lambda _: None)
            job = record["id"]
            self.assertEqual(Path(seen["sourceImage"]), setup.owned(root, f"artifacts/{job}/source.jpg"))
            stored = json.loads((root / "jobs" / f"{job}.json").read_text())["settings"]
            self.assertEqual((stored["sourceImage"], stored["strength"], stored["width"], stored["height"]), (f"{job}/source.jpg", 0.35, 768, 512))
            self.assertNotIn("My Pictures", json.dumps(stored))  # The user's own path is not recorded.
            original.unlink()
            listed = setup.library({"dataRoot": str(root)})["artifacts"][0]["settings"]
            self.assertEqual(Path(listed["sourceImage"]), (root / "artifacts" / job / "source.jpg").resolve())

    def test_upscale_is_a_library_job_with_its_own_log(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            picture = Path(folder) / "small.png"
            picture.write_bytes(b"png bytes")
            calls = []
            def child(app_, data, operation, payload, cancel, progress, log_id=None):
                calls.append((operation, payload["scale"], Path(payload["sourceImage"]).read_bytes(), log_id))
                (data / "logs").mkdir(exist_ok=True)
                (data / "logs" / f"upscale-{log_id}.log").write_text("tiles")
                result = fake_generation(app_, data, operation, payload, cancel, progress)
                return dict(result, width=2048, height=2048, scale=payload["scale"])
            with patch.object(setup, "inference", side_effect=child):
                record = setup.upscale(app, {"dataRoot": str(root), "sourceImage": str(picture), "scale": 4}, threading.Event(), lambda _: None)
            self.assertEqual(calls, [("upscale", 4, b"png bytes", record["id"])])
            listed = setup.library({"dataRoot": str(root)})["artifacts"][0]
            self.assertEqual((listed["kind"], listed["settings"]["scale"], listed["width"]), ("upscale", 4, 2048))
            self.assertEqual(Path(listed["settings"]["sourceImage"]).read_bytes(), b"png bytes")
            self.assertEqual(setup.job_log(root, json.loads((root / "jobs" / f"{record['id']}.json").read_text())).name, f"upscale-{record['id']}.log")

    def test_upscale_requests_are_validated_before_any_record(self):
        with tempfile.TemporaryDirectory() as folder:
            picture = Path(folder) / "photo.webp"
            picture.write_bytes(b"webp")
            for bad in [{"scale": 3}, {"scale": True}, {"scale": "4"}, {"sourceImage": None}, {"sourceImage": str(Path(folder) / "gone.png")}]:
                with self.subTest(bad=bad), self.assertRaises(ValueError):
                    setup.upscale(ROOT, dict({"sourceImage": str(picture), "scale": 2}, dataRoot=str(Path(folder) / "data"), **bad),
                                  threading.Event(), lambda _: None)
            self.assertFalse((Path(folder) / "data").exists())

    def test_upscaler_weights_are_verified_and_watched(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            ready = json.loads(setup.state_path(root).read_text())
            self.assertIn(f"{setup.UPSCALER}/RealESRGAN_x4plus.pth", ready["files"])
            (root / "models" / setup.UPSCALER / "RealESRGAN_x4plus.pth").write_bytes(b"swapped weights!")
            self.assertFalse(setup.files_unchanged(app, root, ready))

    def test_starting_picture_is_validated_before_any_record(self):
        with tempfile.TemporaryDirectory() as folder:
            picture = Path(folder) / "photo.png"
            picture.write_bytes(b"png")
            (Path(folder) / "notes.txt").write_text("x")
            for bad in [{"sourceImage": "photo.png"}, {"sourceImage": str(Path(folder) / "missing.png")},
                        {"sourceImage": str(Path(folder) / "notes.txt")}, {"sourceImage": str(picture), "strength": 0},
                        {"sourceImage": str(picture), "strength": 1.5}, {"sourceImage": 7}]:
                with self.subTest(bad=bad), self.assertRaises(ValueError):
                    setup.validate_generation(dict(bad, prompt="house"))
            self.assertEqual(setup.validate_generation({"prompt": "house", "sourceImage": str(picture)})["strength"], 0.6)
            self.assertNotIn("sourceImage", setup.validate_generation({"prompt": "house", "sourceImage": ""}))
        self.assertEqual([setup.shape_for(*size) for size in [(1600, 900), (900, 1600), (1000, 1100)]], [(768, 512), (512, 768), (512, 512)])

    def test_changed_model_file_blocks_generation_and_revokes_ready(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            (root / "models" / setup.RECIPE / "unet/weights.bin").write_bytes(b"tampered weights")
            with patch.object(setup, "inference", side_effect=AssertionError("inference ran")):
                with self.assertRaisesRegex(setup.SetupError, "changed since"):
                    setup.generate(app, {"dataRoot": str(root), "prompt": "house"}, threading.Event(), lambda _: None)
            self.assertEqual(setup.status(app, {"dataRoot": str(root)})["status"], "needs_verification")

    def test_ready_record_without_stamps_needs_verification(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            record = json.loads(setup.state_path(root).read_text())
            del record["files"]
            setup.atomic_json(setup.state_path(root), record)
            self.assertEqual(setup.status(app, {"dataRoot": str(root)})["status"], "needs_verification")


NATIVE_CORE = setup.native_core_path(ROOT)


class InstallGateTests(unittest.TestCase):
    @unittest.skipUnless(NATIVE_CORE, "build the native core first (scripts/check.ps1 does)")
    def test_bundled_recipe_passes_the_native_gate(self):
        self.assertTrue(setup.plan_installation(ROOT)["canExecute"])

    @unittest.skipUnless(NATIVE_CORE, "build the native core first (scripts/check.ps1 does)")
    def test_blocked_recipe_never_starts_an_attempt(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            recipe = json.loads((app / "recipes/sd15-baseline.json").read_text())
            (app / "recipes/sd15-baseline.json").write_text(json.dumps(dict(recipe, status="draft")))
            with patch.object(setup, "native_core_path", return_value=NATIVE_CORE):
                with self.assertRaisesRegex(setup.SetupError, "may not be installed"):
                    setup.install(app, {"dataRoot": str(root)}, threading.Event(), lambda _: None)
            self.assertFalse(setup.state_path(root).exists())

    def test_missing_native_core_blocks_install(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            with self.assertRaisesRegex(setup.SetupError, "planner is missing"):
                setup.install(app, {"dataRoot": str(root)}, threading.Event(), lambda _: None)
            self.assertFalse(setup.state_path(root).exists())

    def test_reverification_needs_space_only_for_missing_files(self):
        with tempfile.TemporaryDirectory() as folder:
            app, root = fake_app(folder), Path(folder) / "data"
            ready_install(app, root)
            self.assertEqual(setup.required_free_bytes(app, root), setup.FRESH_SETUP_BYTES)  # Runtime not yet recorded as synced.
            setup.runtime_marker(root).write_text(setup.lock_digest(app))
            self.assertEqual(setup.required_free_bytes(app, root), setup.SETUP_HEADROOM_BYTES)
            (root / "models" / setup.RECIPE / "unet/weights.bin").unlink()
            self.assertEqual(setup.required_free_bytes(app, root), setup.SETUP_HEADROOM_BYTES + len(b"weights"))
            with (app / "profiles/sd15-cu126.lock").open("a") as lock:
                lock.write("# changed pin\n")
            self.assertEqual(setup.required_free_bytes(app, root), setup.FRESH_SETUP_BYTES)


class GpuTests(unittest.TestCase):
    LISTING = "0, GPU-aaaa, NVIDIA Small, 4096\n1, GPU-bbbb, NVIDIA Large, 6144\n"

    def test_vram_minimum_comes_from_recipe(self):
        recipe = json.loads((ROOT / "recipes/sd15-baseline.json").read_text())
        minimum = setup.minimum_vram_mib(ROOT)
        self.assertEqual(minimum, recipe["minimumVramMiB"])
        self.assertTrue(setup.meets_vram(6442123264 / 1024**2, minimum))  # 6 GB GTX 1660 Ti as CUDA reports it
        self.assertTrue(setup.meets_vram(6144, minimum))  # The same card as nvidia-smi reports it
        self.assertFalse(setup.meets_vram(5632, minimum))

    def test_preflight_inspects_the_device_the_child_uses(self):
        self.assertEqual(setup.select_gpu(self.LISTING), ("NVIDIA Small", 4096))
        self.assertEqual(setup.select_gpu(self.LISTING, "1"), ("NVIDIA Large", 6144))
        self.assertEqual(setup.select_gpu(self.LISTING, "GPU-bbbb,0"), ("NVIDIA Large", 6144))
        for hidden in ["", "7"]:
            with self.assertRaises(setup.SetupError):
                setup.select_gpu(self.LISTING, hidden)

    def test_unreported_memory_is_a_clear_setup_error(self):
        with self.assertRaisesRegex(setup.SetupError, r"did not report memory.*\[N/A\]"):
            setup.select_gpu("0, GPU-aaaa, NVIDIA Odd, [N/A]\n")
        with self.assertRaises(setup.SetupError):
            setup.select_gpu("")

    @unittest.skipUnless(os.name == "nt", "install preflight runs only on Windows")
    def test_install_reports_unparseable_gpu_memory_as_setup_failure(self):
        listing = subprocess.CompletedProcess([], 0, stdout="0, GPU-aaaa, NVIDIA Odd, [N/A]\n", stderr="")
        environment = {k: v for k, v in os.environ.items() if k != "CUDA_VISIBLE_DEVICES"}
        with tempfile.TemporaryDirectory() as folder, \
             patch.object(setup.shutil, "disk_usage", return_value=type("Usage", (), {"free": 100 * 1024**3})()), \
             patch.object(setup.shutil, "which", return_value="nvidia-smi"), \
             patch.object(setup.subprocess, "run", return_value=listing), \
             patch.dict(os.environ, environment, clear=True):
            with self.assertRaisesRegex(setup.SetupError, "did not report memory"):
                setup.install(ROOT, {"dataRoot": folder}, threading.Event(), lambda _: None)
            self.assertEqual(json.loads(setup.state_path(Path(folder)).read_text())["status"], "failed")


class LibraryTests(unittest.TestCase):
    JOB = "d" * 32

    def write_job(self, root, image_path, job_id=JOB):
        setup.atomic_json(root / "jobs" / f"{job_id}.json", {"id": job_id, "status": "succeeded", "imagePath": image_path})

    def image(self, root, job_id=JOB):
        path = root / "artifacts" / job_id / "image.png"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"png")
        return path

    def test_relative_image_resolves_inside_artifacts(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            image = self.image(root)
            self.write_job(root, f"{self.JOB}/image.png")
            listed = setup.library({"dataRoot": folder})["artifacts"]
            self.assertEqual([Path(a["imagePath"]) for a in listed], [image.resolve()])

    def test_traversal_and_foreign_paths_are_not_listed(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "escape.png").write_bytes(b"png")
            self.image(root)
            self.write_job(root, f"{self.JOB}/../../escape.png")
            self.write_job(root, str(root / "artifacts" / self.JOB / ".." / ".." / "escape.png"), "e" * 32)
            self.write_job(root, f"{self.JOB}/image.png", "f" * 32)  # Another job's folder.
            self.assertEqual(setup.library({"dataRoot": folder})["artifacts"], [])

    def test_unfinished_jobs_are_listed_with_reason_and_own_log(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            failed, interrupted = "1" * 32, "2" * 32
            setup.atomic_json(root / "jobs" / f"{failed}.json", {"id": failed, "status": "failed", "message": "CUDA out of memory",
                "startedUtc": "2026-10-17T00:00:00+00:00", "settings": {"prompt": "house"}, "log": f"generate-{failed}.log"})
            setup.atomic_json(root / "jobs" / f"{interrupted}.json", {"id": interrupted, "status": "running", "log": "../../escape.log"})
            (root / "logs").mkdir()
            (root / "logs" / f"generate-{failed}.log").write_text("trace")
            setup.atomic_json(setup.state_path(root), {"status": "failed", "message": "Setup failed"})
            listing = setup.library({"dataRoot": folder})
            problems = {p["id"]: p for p in listing["problems"]}
            self.assertEqual(set(problems), {failed, interrupted})  # The setup attempt is not a generation job.
            self.assertEqual(problems[failed]["message"], "CUDA out of memory")
            self.assertEqual(Path(problems[failed]["logPath"]), (root / "logs" / f"generate-{failed}.log").resolve())
            self.assertEqual((problems[interrupted]["status"], problems[interrupted]["logPath"]), ("interrupted", None))

    def test_failures_do_not_push_images_out_of_the_library(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.image(root)
            self.write_job(root, f"{self.JOB}/image.png")
            old = (root / "jobs" / f"{self.JOB}.json").stat().st_mtime - 100
            os.utime(root / "jobs" / f"{self.JOB}.json", (old, old))
            for i in range(setup.LIBRARY_IMAGES + 10):
                job_id = f"{i:032x}"
                setup.atomic_json(root / "jobs" / f"{job_id}.json", {"id": job_id, "status": "failed", "message": "x"})
            listing = setup.library({"dataRoot": folder})
            self.assertEqual([a["id"] for a in listing["artifacts"]], [self.JOB])
            self.assertEqual(len(listing["problems"]), setup.LIBRARY_PROBLEMS)

    def test_legacy_absolute_records_survive_a_moved_data_folder(self):
        with tempfile.TemporaryDirectory() as folder:
            old, moved = Path(folder) / "old", Path(folder) / "moved"
            self.image(old)
            self.write_job(old, str(old / "artifacts" / self.JOB / "image.png"))
            shutil.move(old, moved)
            listed = setup.library({"dataRoot": str(moved)})["artifacts"]
            self.assertEqual([Path(a["imagePath"]) for a in listed], [(moved / "artifacts" / self.JOB / "image.png").resolve()])


if __name__ == "__main__":
    unittest.main()
