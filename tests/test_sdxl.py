"""Stable Diffusion XL setup, runtime choices, and generation routing with fake GPU children; never downloads weights."""
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
import hardware  # noqa: E402
import inference_setup as setup  # noqa: E402
import model_library  # noqa: E402
import sdxl  # noqa: E402
from test_inference_setup import fake_app, ready_install  # noqa: E402

XL_FILES = {"model_index.json": b"{}", "unet/diffusion_pytorch_model.fp16.safetensors": b"xl unet"}
GTX = dict(name="NVIDIA GeForce GTX 1660 Ti", vramMiB=6144, computeCapability="7.5", driver="580.88")
RTX = dict(name="NVIDIA GeForce RTX 4060", vramMiB=8188, computeCapability="8.9", driver="580.88")


def xl_app(folder):
    app = fake_app(folder)
    for name in ("profiles/sdxl-files.json", "profiles/video-files.json", "profiles/storage-layout.json"):
        shutil.copyfile(ROOT / name, app / name)
    manifest = json.loads((app / "profiles/sdxl-files.json").read_text())
    manifest["files"] = [dict(path=path, size=len(body), sha256=hashlib.sha256(body).hexdigest()) for path, body in XL_FILES.items()]
    (app / "profiles/sdxl-files.json").write_text(json.dumps(manifest))
    return app


def fake_child(app, root, operation, payload, cancel, progress, log_id=None):
    if operation == "download":
        assert payload == {"manifest": "sdxl-files.json"}, payload
        for path, body in XL_FILES.items():
            target = root / "models" / sdxl.MODEL / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(body)
        return {"downloaded": True}
    if operation == "verify-xl":
        assert (payload["width"], payload["height"], payload["model"]) == (1024, 1024, sdxl.MODEL)
        return dict(gpu="Test GPU", seconds=60.0, loadSeconds=20.0, width=1024, height=1024)
    assert operation == "generate", operation
    image = root / "artifacts" / payload["jobId"] / "image.png"
    image.parent.mkdir(parents=True, exist_ok=True)
    image.write_bytes(b"png")
    return dict(imagePath=payload["jobId"] + "/image.png", imageSha256="0" * 64, seconds=90.0, gpu="Test GPU",
                width=payload["width"], height=payload["height"], runtime=payload["runtime"], model=payload["model"])


class SdxlTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.app, self.root = xl_app(self.folder), self.folder / "data"
        native = patch("file_verification.native_core_path", return_value=setup.native_core_path(ROOT))
        native.start()
        self.addCleanup(native.stop)

    def install(self, gpu=GTX, child=fake_child, ram=65536):
        with patch.object(hardware, "detect", return_value=dict(gpu=gpu, ramMiB=ram, drive=dict(kind="ssd"))), \
             patch.object(setup, "inference", side_effect=child):
            return sdxl.install(self.app, {"dataRoot": str(self.root)}, threading.Event(), lambda _: None)

    def test_each_card_gets_an_sdxl_runtime_and_full_precision_needs_memory(self):
        choose = lambda gpu, ram=32768: hardware.choose_xl(dict(gpu=gpu, ramMiB=ram), 6144)
        self.assertEqual(choose(GTX)["runtime"], hardware.XL_CONSERVATIVE)
        self.assertIn("several minutes", " ".join(choose(GTX)["reasons"]))
        self.assertEqual(choose(RTX)["runtime"], "xl-fp16-model")
        self.assertEqual(choose(dict(RTX, vramMiB=6144, name="NVIDIA GeForce RTX 2060"))["runtime"], "xl-fp16-sequential")
        blocked = choose(GTX, ram=8192)
        self.assertIsNone(blocked["runtime"])
        self.assertIn("16 GB", blocked["blocked"])
        self.assertEqual(choose(RTX, ram=8192)["runtime"], "xl-fp16-model", "Half precision needs no extra memory")
        self.assertEqual(list(hardware.XL_RUNTIMES)[-1], hardware.XL_CONSERVATIVE)

    def test_setup_downloads_verifies_and_tests_at_1024_before_it_is_listed_ready(self):
        self.assertEqual(sdxl.status(self.app, {"dataRoot": str(self.root)})["status"], "not_installed")
        with self.assertRaisesRegex(setup.SetupError, "Stable Diffusion 1.5 first"):
            self.install()
        ready_install(self.app, self.root)
        result = self.install()
        self.assertEqual((result["status"], result["runtime"]), ("ready", hardware.XL_CONSERVATIVE))
        listed = model_library.listing(self.app, {"dataRoot": str(self.root)})["models"]
        self.assertEqual([model["id"] for model in listed[:2]], [setup.RECIPE, sdxl.MODEL])
        self.assertEqual((listed[1]["status"], listed[1]["builtIn"], listed[1]["format"]), ("ready", True, "bundled-xl"))
        (self.root / "models" / sdxl.MODEL / "model_index.json").write_bytes(b"[]")
        self.assertEqual(sdxl.status(self.app, {"dataRoot": str(self.root)})["status"], "needs_verification")

    def test_a_faster_choice_that_fails_falls_back(self):
        ready_install(self.app, self.root)
        def child(app, root, operation, payload, cancel, progress, log_id=None):
            if operation == "verify-xl" and payload["runtime"] == "xl-fp16-model":
                raise setup.SetupError("CUDA error: out of memory")
            return fake_child(app, root, operation, payload, cancel, progress)
        result = self.install(gpu=RTX, child=child)
        self.assertEqual(result["runtime"], hardware.XL_CONSERVATIVE)
        record = json.loads(sdxl.XL.state_path(self.root).read_text())
        self.assertEqual([item["runtime"] for item in record["declinedRuntimes"]], ["xl-fp16-model"])

    def test_generation_with_sdxl_uses_its_runtime_sizes_and_record(self):
        ready_install(self.app, self.root)
        with self.assertRaisesRegex(setup.SetupError, "Set up Stable Diffusion XL"):
            setup.generate(self.app, {"dataRoot": str(self.root), "prompt": "castle", "model": sdxl.MODEL, "width": 1024, "height": 1024},
                           threading.Event(), lambda _: None)
        self.install()
        for bad in [dict(width=512, height=512), dict(width=1216, height=1216)]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                setup.validate_generation(dict(bad, prompt="castle", model=sdxl.MODEL))
        with self.assertRaises(ValueError):
            setup.validate_generation(dict(prompt="castle", width=1024, height=1024))
        seen = {}
        def child(app, root, operation, payload, cancel, progress, log_id=None):
            seen.update(payload)
            return fake_child(app, root, operation, payload, cancel, progress)
        with patch.object(setup, "inference", side_effect=child):
            record = setup.generate(self.app, {"dataRoot": str(self.root), "prompt": "castle", "model": sdxl.MODEL, "width": 1216, "height": 832},
                                    threading.Event(), lambda _: None)
        self.assertEqual((seen["model"], seen["runtime"]), (sdxl.MODEL, hardware.XL_CONSERVATIVE))
        stored = json.loads((self.root / "jobs" / f"{record['id']}.json").read_text())
        self.assertEqual((stored["model"], stored["modelName"], stored["settings"]["width"], stored["source"]["repoId"]),
                         (sdxl.MODEL, "Stable Diffusion XL", 1216, "stabilityai/stable-diffusion-xl-base-1.0"))

    def test_pictures_from_a_starting_picture_take_the_nearest_sdxl_shape(self):
        self.assertEqual(setup.shape_for(1600, 900, xl=True), (1216, 832))
        self.assertEqual(setup.shape_for(900, 1600, xl=True), (832, 1216))
        self.assertEqual(setup.shape_for(1000, 1000, xl=True), (1024, 1024))
        box, shape = setup.edit_region((100, 100, 200, 200), (2000, 1500), xl=True)
        self.assertIn(shape, setup.XL_SIZES)
        self.assertGreaterEqual(max(box[2] - box[0], box[3] - box[1]), 768)

    def test_removing_sdxl_keeps_its_pictures(self):
        ready_install(self.app, self.root)
        self.install()
        with patch.object(setup, "inference", side_effect=fake_child):
            made = setup.generate(self.app, {"dataRoot": str(self.root), "prompt": "castle", "model": sdxl.MODEL, "width": 1024, "height": 1024},
                                  threading.Event(), lambda _: None)
        self.assertEqual(sdxl.remove(self.app, {"dataRoot": str(self.root)})["status"], "not_installed")
        self.assertFalse((self.root / "models" / sdxl.MODEL).exists())
        self.assertTrue(Path(made["imagePath"]).is_file())


if __name__ == "__main__":
    unittest.main()
