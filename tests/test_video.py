"""Short-video setup, generation records, encoding, and clean-up with fake GPU children; never downloads weights."""
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
sys.path.insert(0, str(ROOT / "tests"))
import artifact_export  # noqa: E402
import hardware  # noqa: E402
import inference_setup as setup  # noqa: E402
import library_index  # noqa: E402
import video  # noqa: E402
from test_inference_setup import fake_app, ready_install  # noqa: E402

MOTION_FILES = {"README.md": b"# motion card", "config.json": b"{}", "diffusion_pytorch_model.safetensors": b"motion weights"}
GTX = dict(name="NVIDIA GeForce GTX 1660 Ti", vramMiB=6144, computeCapability="7.5", driver="580.88")
RTX = dict(name="NVIDIA GeForce RTX 3060", vramMiB=12288, computeCapability="8.6", driver="580.88")


def video_app(folder):
    app = fake_app(folder)
    for name in ("profiles/video-files.json", "profiles/storage-layout.json"):
        shutil.copyfile(ROOT / name, app / name)
    manifest = json.loads((app / "profiles/video-files.json").read_text())
    manifest["files"] = [dict(name=name, url="https://example.invalid/" + name, size=len(body),
                              sha256=hashlib.sha256(body).hexdigest()) for name, body in MOTION_FILES.items()]
    (app / "profiles/video-files.json").write_text(json.dumps(manifest))
    return app


def fake_download(entry, destination, cancel, progress):
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(MOTION_FILES[entry["name"]])


def write_frames(folder, width, height, frames):
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "frames.rgb").write_bytes(bytes(range(256)) * (width * height * 3 * frames // 256))
    (folder / "poster.png").write_bytes(b"poster")


def fake_child(app, root, operation, payload, cancel, progress, log_id=None):
    """Stands in for the ML child: frames and a poster where the real one writes them."""
    if operation == "verify-video":
        write_frames(Path(payload["outputFolder"]), payload["width"], payload["height"], payload["frames"])
        return dict(gpu="Test GPU", seconds=2.0, loadSeconds=1.0, width=payload["width"], height=payload["height"], frames=payload["frames"])
    assert operation == "video", operation
    folder = root / "artifacts" / payload["jobId"]
    write_frames(folder, payload["width"], payload["height"], payload["frames"])
    return dict(gpu="Test GPU", seconds=30.0, loadSeconds=4.0, width=payload["width"], height=payload["height"],
                frames=payload["frames"], imagePath=payload["jobId"] + "/poster.png",
                imageSha256=hashlib.sha256(b"poster").hexdigest(), runtime=payload["runtime"])


def fake_encode(app, frames_path, width, height, frames, destination, cancel):
    assert frames_path.stat().st_size == width * height * 3 * frames
    destination.write_bytes(b"\0\0\0\x18ftypisom" + b"\0" * 64)
    return hashlib.sha256(destination.read_bytes()).hexdigest()


class VideoTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.app, self.root = video_app(self.folder), self.folder / "data"
        native = patch("file_verification.native_core_path", return_value=setup.native_core_path(ROOT))
        native.start()
        self.addCleanup(native.stop)

    def detected(self, gpu=GTX):
        return patch.object(hardware, "detect", return_value=dict(gpu=gpu, ramMiB=65536, drive=dict(kind="ssd")))

    def install(self, gpu=GTX, child=fake_child, encode=fake_encode):
        with self.detected(gpu), patch.object(setup, "download", side_effect=fake_download), \
             patch.object(setup, "inference", side_effect=child), patch.object(video, "encode", side_effect=encode):
            return video.install(self.app, {"dataRoot": str(self.root)}, threading.Event(), lambda _: None)

    def generate(self, **settings):
        with patch.object(setup, "inference", side_effect=fake_child), patch.object(video, "encode", side_effect=fake_encode):
            return video.generate(self.app, dict({"dataRoot": str(self.root), "prompt": "waves at dusk"}, **settings),
                                  threading.Event(), lambda _: None)

    def test_each_card_gets_a_video_runtime_it_can_hold(self):
        choose = lambda gpu: hardware.choose_video(dict(gpu=gpu, ramMiB=32768), 6144)
        self.assertEqual(choose(GTX)["runtime"], "video-fp32-sequential")
        self.assertIn("several minutes", " ".join(choose(GTX)["reasons"]))
        self.assertEqual(choose(dict(RTX, vramMiB=6144, name="NVIDIA GeForce RTX 2060"))["runtime"], "video-fp16-sequential")
        self.assertEqual(choose(RTX)["runtime"], "video-fp16-model")
        pascal = dict(name="NVIDIA GeForce GTX 1080 Ti", computeCapability="6.1", driver="1")
        self.assertEqual(choose(dict(pascal, vramMiB=11264))["runtime"], "video-fp32-sequential")
        self.assertEqual(choose(dict(pascal, vramMiB=12288, name="NVIDIA TITAN Xp"))["runtime"], "video-fp32-model")
        self.assertIsNotNone(choose(dict(GTX, vramMiB=4096))["blocked"])
        self.assertEqual(list(hardware.VIDEO_RUNTIMES)[-1], hardware.VIDEO_CONSERVATIVE)

    def test_videos_need_stable_diffusion_first_and_say_so(self):
        self.assertEqual(video.status(self.app, {"dataRoot": str(self.root)})["status"], "not_installed")
        with self.assertRaisesRegex(setup.SetupError, "Stable Diffusion 1.5 first"):
            self.install()
        self.assertFalse(video.state_path(self.root).exists())
        ready_install(self.app, self.root)
        with self.assertRaisesRegex(setup.SetupError, "Set up videos"):
            self.generate()
        self.assertEqual(list((self.root / "jobs").glob("????????????????????????????????.json")), [])

    def test_setup_verifies_files_tests_a_clip_and_encodes_it_before_ready(self):
        ready_install(self.app, self.root)
        seen = []
        def child(*args, **kwargs):
            seen.append(args[3]["runtime"])
            return fake_child(*args, **kwargs)
        result = self.install(child=child)
        self.assertEqual((result["status"], result["runtime"], seen), ("ready", hardware.VIDEO_CONSERVATIVE, [hardware.VIDEO_CONSERVATIVE]))
        self.assertNotIn("files", result)
        self.assertEqual(result["settings"]["label"], "Memory-saving")
        self.assertEqual(video.status(self.app, {"dataRoot": str(self.root)})["status"], "ready")
        self.assertEqual(list((self.root / "cache/video-test").iterdir()), [], "The test clip is removed")
        # Changing a verified file revokes readiness without re-hashing it every time.
        weights = self.root / "models" / video.MOTION / "diffusion_pytorch_model.safetensors"
        weights.write_bytes(b"other weights!")
        self.assertEqual(video.status(self.app, {"dataRoot": str(self.root)})["status"], "needs_verification")

    def test_a_faster_choice_that_fails_falls_back_and_is_remembered(self):
        ready_install(self.app, self.root)
        def child(app, root, operation, payload, cancel, progress, log_id=None):
            if payload["runtime"] == "video-fp16-model":
                raise setup.SetupError("CUDA error: out of memory")
            return fake_child(app, root, operation, payload, cancel, progress)
        result = self.install(gpu=RTX, child=child)
        self.assertEqual(result["runtime"], hardware.VIDEO_CONSERVATIVE)
        self.assertIn("Faster settings did not pass", result["message"])
        record = json.loads(video.state_path(self.root).read_text())
        self.assertEqual([item["runtime"] for item in record["declinedRuntimes"]], ["video-fp16-model"])
        tried = []
        def again(app, root, operation, payload, cancel, progress, log_id=None):
            tried.append(payload["runtime"])
            return fake_child(app, root, operation, payload, cancel, progress)
        self.install(gpu=RTX, child=again)
        self.assertEqual(tried, [hardware.VIDEO_CONSERVATIVE], "The same card and driver do not retry it")

    def test_a_missing_windows_encoder_is_explained_and_never_ready(self):
        ready_install(self.app, self.root)
        def missing(*args):
            raise video.EncoderMissing(video.ENCODER_MESSAGE)
        with self.assertRaisesRegex(setup.SetupError, "Media Feature Pack"):
            self.install(gpu=RTX, encode=missing)
        self.assertEqual(json.loads(video.state_path(self.root).read_text())["status"], "failed")
        self.assertNotIn("declinedRuntimes", json.loads(video.state_path(self.root).read_text()) or {})

    def test_generation_saves_an_mp4_and_poster_and_lists_it_as_a_video(self):
        ready_install(self.app, self.root)
        self.install()
        result = self.generate(frames=24, width=768, height=512, sampler="euler-a")
        folder = self.root / "artifacts" / result["id"]
        self.assertEqual((result["kind"], result["frames"], result["fps"], result["settings"]["sampler"]), ("video", 24, 8, "ddim"))
        self.assertEqual(Path(result["videoPath"]), (folder / "video.mp4").resolve())
        self.assertFalse((folder / "frames.rgb").exists(), "Raw frames are removed once encoded")
        stored = json.loads((self.root / "jobs" / f"{result['id']}.json").read_text())
        self.assertEqual((stored["videoPath"], stored["log"], stored["runtime"]),
                         (f"{result['id']}/video.mp4", f"video-{result['id']}.log", hardware.VIDEO_CONSERVATIVE))
        listing = library_index.listing({"dataRoot": str(self.root), "kind": "video"})
        self.assertEqual([item["id"] for item in listing["artifacts"]], [result["id"]])
        self.assertEqual(listing["artifacts"][0]["videoPath"], result["videoPath"])
        self.assertEqual(library_index.listing({"dataRoot": str(self.root), "kind": "upscale"})["total"], 0)
        (folder / "video.mp4").unlink()
        self.assertEqual(library_index.listing({"dataRoot": str(self.root)})["total"], 0, "A video without its MP4 is not listed")

    def test_video_settings_are_validated_before_anything_is_written(self):
        for bad in [dict(frames=12), dict(frames=True), dict(model="e" * 32), dict(prompt="")]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                video.validate(dict({"prompt": "waves"}, **bad))
        settings = video.validate({"prompt": "waves", "sourceImage": "C:/ignored.png"})
        self.assertNotIn("sourceImage", settings)
        self.assertEqual((settings["frames"], settings["fps"]), (16, 8))

    def test_a_failed_encode_leaves_no_frames_poster_or_listing(self):
        ready_install(self.app, self.root)
        self.install()
        def broken(app, frames_path, width, height, frames, destination, cancel):
            raise setup.SetupError("The video could not be written (3).")
        with patch.object(setup, "inference", side_effect=fake_child), patch.object(video, "encode", side_effect=broken), \
             self.assertRaisesRegex(setup.SetupError, "could not be written"):
            video.generate(self.app, {"dataRoot": str(self.root), "prompt": "waves"}, threading.Event(), lambda _: None)
        job = next(path for path in (self.root / "jobs").glob("*.json") if len(path.stem) == 32)
        self.assertEqual(json.loads(job.read_text())["status"], "failed")
        self.assertEqual(list((self.root / "artifacts" / job.stem).iterdir()), [])

    def test_an_interrupted_video_job_is_recovered_without_its_frames(self):
        job_id = "a" * 32
        folder = self.root / "artifacts" / job_id
        write_frames(folder, 64, 64, 2)
        setup.atomic_json(self.root / "jobs" / f"{job_id}.json", dict(id=job_id, kind="video", status="running", settings={}))
        self.assertEqual(setup.recover_if_idle(self.root), 1)
        record = json.loads((self.root / "jobs" / f"{job_id}.json").read_text())
        self.assertEqual(record["status"], "interrupted")
        self.assertIn("no video", record["message"])
        self.assertEqual(list(folder.iterdir()), [])

    def test_saving_a_copy_exports_the_verified_mp4(self):
        ready_install(self.app, self.root)
        self.install()
        result = self.generate()
        target = self.folder / "out" / "clip.mp4"
        target.parent.mkdir()
        payload = {"dataRoot": str(self.root), "jobId": result["id"], "destination": str(target)}
        saved = artifact_export.export(ROOT, payload, threading.Event(), lambda _: None)
        self.assertEqual(target.read_bytes(), Path(result["videoPath"]).read_bytes())
        self.assertEqual(saved["sha256"], result["videoSha256"])
        with self.assertRaisesRegex(ValueError, "MP4"):
            artifact_export.export(ROOT, dict(payload, destination=str(target.with_suffix(".png"))), threading.Event(), lambda _: None)

    def test_removing_videos_keeps_the_videos_already_made(self):
        ready_install(self.app, self.root)
        self.install()
        made = self.generate()
        self.assertEqual(video.remove(self.app, {"dataRoot": str(self.root)})["status"], "not_installed")
        self.assertFalse((self.root / "models" / video.MOTION).exists())
        self.assertTrue(Path(made["videoPath"]).is_file())


class EncoderTests(unittest.TestCase):
    def test_the_native_encoder_writes_an_mp4_or_reports_the_missing_encoder(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            (folder / "frames.rgb").write_bytes(bytes(64 * 64 * 3 * 4))
            try:
                digest = video.encode(ROOT, folder / "frames.rgb", 64, 64, 4, folder / "clip.mp4", threading.Event())
            except video.EncoderMissing:
                self.skipTest("Windows Media Foundation is not available on this machine")
            self.assertEqual(digest, hashlib.sha256((folder / "clip.mp4").read_bytes()).hexdigest())
            self.assertEqual((folder / "clip.mp4").read_bytes()[4:8], b"ftyp")
            cancelled = threading.Event()
            cancelled.set()
            with self.assertRaises(setup.Cancelled):
                video.encode(ROOT, folder / "frames.rgb", 64, 64, 4, folder / "other.mp4", cancelled)
            self.assertEqual(sorted(path.name for path in folder.iterdir()), ["clip.mp4", "frames.rgb"])


if __name__ == "__main__":
    unittest.main()
