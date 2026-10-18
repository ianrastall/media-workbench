"""Adding models the user already downloaded: header identification, refusals, copying, and readiness.

Fake safetensors files declare real SD 1.5 tensor shapes; their tensor bytes are sparse zeros, so
no model weights are downloaded or loaded and no ML runtime is needed.
"""
import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_setup as setup
import model_library as models
import storage
from test_inference_setup import fake_app, fake_generation, ready_install

NATIVE_CORE = setup.native_core_path(ROOT)
UNET = "model.diffusion_model."
CHECKPOINT = {
    UNET + "input_blocks.0.0.weight": ("F16", [320, 4, 3, 3]),
    UNET + "input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight": ("F16", [320, 768]),
    UNET + "out.2.weight": ("F16", [4, 320, 3, 3]),
    "cond_stage_model.transformer.text_model.embeddings.token_embedding.weight": ("F16", [49408, 768]),
    "cond_stage_model.transformer.text_model.embeddings.position_ids": ("I64", [1, 77]),
    "first_stage_model.decoder.conv_in.weight": ("F16", [512, 4, 3, 3]),
    "first_stage_model.encoder.conv_out.weight": ("F16", [8, 512, 3, 3]),
}
FOLDER = {
    "unet/diffusion_pytorch_model.fp16.safetensors": {
        "conv_in.weight": ("F16", [320, 4, 3, 3]), "conv_out.weight": ("F16", [4, 320, 3, 3]),
        "down_blocks.0.attentions.0.transformer_blocks.0.attn2.to_k.weight": ("F16", [320, 768])},
    "text_encoder/model.fp16.safetensors": {"text_model.embeddings.token_embedding.weight": ("F16", [49408, 768]),
                                            "text_model.embeddings.position_ids": ("I64", [1, 77])},
    "vae/diffusion_pytorch_model.fp16.safetensors": {
        "decoder.conv_in.weight": ("F16", [512, 4, 3, 3]), "encoder.conv_out.weight": ("F16", [8, 512, 3, 3])},
}
SIZES = {"F16": 2, "F32": 4, "BF16": 2, "F8_E4M3": 1, "I64": 8}


def write_safetensors(path, tensors, truncate_by=0, salt=b""):
    """A valid safetensors layout whose tensor data is zeros (written sparsely)."""
    header, offset = {"__metadata__": {"format": "pt"}}, 0
    for name, (dtype, shape) in tensors.items():
        size = SIZES[dtype]
        for side in shape:
            size *= side
        header[name] = {"dtype": dtype, "shape": shape, "data_offsets": [offset, offset + size]}
        offset += size
    raw = json.dumps(header).encode()
    raw += b" " * (-len(raw) % 8)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as stream:
        stream.write(struct.pack("<Q", len(raw)) + raw + salt)  # A salt gives distinct bytes in the same layout.
        stream.truncate(8 + len(raw) + offset - truncate_by)
    return path


def checkpoint(path, changes=None, remove=(), **options):
    tensors = {name: value for name, value in CHECKPOINT.items() if name not in remove}
    tensors.update(changes or {})
    return write_safetensors(path, tensors, **options)


def diffusers_folder(path, index=None, skip=()):
    path.mkdir(parents=True, exist_ok=True)
    (path / "model_index.json").write_text(json.dumps(index or {"_class_name": "StableDiffusionPipeline"}))
    for name, tensors in FOLDER.items():
        if name.split("/")[0] not in skip:
            write_safetensors(path / name, tensors)
    return path


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class IdentificationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="MediaWorkbench-models-café-")
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)

    def refused(self, path, fragment):
        with self.assertRaises(models.Refused) as raised:
            models.inspect({"path": str(path)})
        self.assertIn(fragment, str(raised.exception))
        return raised.exception

    def test_single_file_checkpoint_is_identified_from_its_header(self):
        path = checkpoint(self.folder / "dreamy_style_v2.safetensors")
        result = models.inspect({"path": str(path)})
        self.assertEqual((result["format"], result["ownVae"], result["suggestedName"]), ("single-file", True, "dreamy style v2"))
        self.assertEqual(result["files"], [{"path": "model.safetensors", "source": str(path.resolve()), "size": path.stat().st_size}])
        self.assertIn("original stays where it is", result["summary"])

    def test_checkpoint_without_vae_uses_the_bundled_decoder(self):
        path = checkpoint(self.folder / "no-vae.safetensors", remove=[n for n in CHECKPOINT if n.startswith("first_stage_model.")])
        result = models.inspect({"path": str(path)})
        self.assertFalse(result["ownVae"])
        self.assertIn("Stable Diffusion 1.5's is used", result["summary"])

    def test_other_model_families_and_add_ons_are_refused_with_what_they_are(self):
        cases = {
            "Stable Diffusion XL": dict(changes={"conditioner.embedders.1.model.ln_final.weight": ("F16", [1280])}),
            "Stable Diffusion 2": dict(changes={UNET + "input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight": ("F16", [320, 1024])}),
            "dedicated inpainting": dict(changes={UNET + "input_blocks.0.0.weight": ("F16", [320, 9, 3, 3])}),
            "InstructPix2Pix": dict(changes={UNET + "input_blocks.0.0.weight": ("F16", [320, 8, 3, 3])}),
            "no text encoder": dict(remove=["cond_stage_model.transformer.text_model.embeddings.token_embedding.weight"]),
            "compressed number format": dict(changes={UNET + "out.2.weight": ("F8_E4M3", [4, 320, 3, 3])}),
        }
        for fragment, options in cases.items():
            with self.subTest(fragment):
                self.refused(checkpoint(self.folder / (fragment + ".safetensors"), **options), fragment)
        add_ons = {
            "LoRA": {"lora_unet_down_blocks_0_attentions_0_proj_in.lora_up.weight": ("F16", [320, 4])},
            "textual-inversion": {"emb_params": ("F32", [2, 768])},
            "FLUX": {"double_blocks.0.img_attn.norm.key_norm.scale": ("BF16", [128])},
            "ControlNet": {"control_model.input_hint_block.0.weight": ("F16", [16, 3, 3, 3])},
            "picture decoder (VAE)": {"encoder.conv_in.weight": ("F16", [128, 3, 3, 3]), "decoder.conv_in.weight": ("F16", [512, 4, 3, 3])},
            "one part of a model": {"conv_in.weight": ("F16", [320, 4, 3, 3]), "down_blocks.0.resnets.0.conv1.weight": ("F16", [320, 320, 3, 3])},
            "not a Stable Diffusion model": {"weight": ("F32", [4])},
        }
        for fragment, tensors in add_ons.items():
            with self.subTest(fragment):
                self.refused(write_safetensors(self.folder / (fragment + ".safetensors"), tensors), fragment)

    def test_damaged_incomplete_and_pickle_files_are_refused_without_loading(self):
        self.refused(checkpoint(self.folder / "partial.safetensors", truncate_by=1000), "still downloading")
        damaged = self.folder / "damaged.safetensors"
        damaged.write_bytes(struct.pack("<Q", 20) + b"{not json at all!!!}" + b"\0" * 64)
        self.refused(damaged, "not a readable")
        huge = self.folder / "huge-header.safetensors"
        huge.write_bytes(struct.pack("<Q", 1 << 40) + b"{}")
        self.refused(huge, "not a readable")
        wrong_size = self.folder / "wrong-size.safetensors"
        raw = json.dumps({"a": {"dtype": "F16", "shape": [4], "data_offsets": [0, 4]}}).encode()
        wrong_size.write_bytes(struct.pack("<Q", len(raw)) + raw + b"\0" * 4)
        self.refused(wrong_size, "damaged header")
        pickle = self.folder / "classic.ckpt"
        pickle.write_bytes(b"\x80\x04 this would be unpickled")
        self.refused(pickle, "can run hidden code")
        self.refused(self.folder / "absent.safetensors", "could not be found")
        with self.assertRaises(ValueError):
            models.inspect({"path": "relative.safetensors"})

    def test_diffusers_folder_is_identified_part_by_part(self):
        snapshot = diffusers_folder(self.folder / "models--someone--dreamy-model" / "snapshots" / ("a" * 40))
        result = models.inspect({"path": str(snapshot)})
        self.assertEqual((result["format"], result["ownVae"], result["suggestedName"]), ("diffusers", True, "dreamy-model"))
        self.assertEqual(sorted(item["path"] for item in result["files"]),
                         ["text_encoder/model.safetensors", "unet/diffusion_pytorch_model.safetensors", "vae/diffusion_pytorch_model.safetensors"])

    def test_unsupported_folders_are_refused(self):
        self.refused(diffusers_folder(self.folder / "xl", {"_class_name": "StableDiffusionXLPipeline"}), "Stable Diffusion XL")
        self.refused(diffusers_folder(self.folder / "flux", {"_class_name": "FluxPipeline"}), "FluxPipeline")
        pickled = diffusers_folder(self.folder / "pickled", skip=["unet"])
        (pickled / "unet").mkdir()
        (pickled / "unet/diffusion_pytorch_model.bin").write_bytes(b"pickle")
        self.refused(pickled, ".bin (pickle)")
        self.refused(diffusers_folder(self.folder / "no-text", skip=["text_encoder"]), "no text encoder weights")
        plain = self.folder / "plain"
        plain.mkdir()
        self.refused(plain, "no model_index.json")

    def test_missing_native_checker_cannot_fall_back_to_a_second_policy(self):
        path = checkpoint(self.folder / "valid.safetensors")
        with patch.object(storage, "library", side_effect=setup.SetupError("Reinstall Media Workbench")):
            with self.assertRaisesRegex(setup.SetupError, "Reinstall"):
                models.inspect({"path": str(path)})

    def test_copied_diffusers_parts_use_the_same_weight_type_gate(self):
        folder = self.folder / "copied"
        for part, (_, relative) in models.FOLDER_PARTS.items():
            tensors = dict(FOLDER[next(name for name in FOLDER if name.startswith(part + "/"))])
            if part == "unet":
                tensors["conv_out.weight"] = ("F8_E4M3", [4, 320, 3, 3])
            write_safetensors(folder / relative, tensors)
        with self.assertRaisesRegex(models.Refused, "compressed number format"):
            models.identify_folder_copy(folder)


class ImportTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="MediaWorkbench-import-森林-")
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.app = fake_app(self.folder)
        self.root = self.folder / "data"
        native = patch("file_verification.native_core_path", return_value=NATIVE_CORE)
        native.start()
        self.addCleanup(native.stop)
        ready_install(self.app, self.root)
        for part in ("unet", "text_encoder", "vae"):
            (self.root / "models" / setup.RECIPE / part).mkdir(parents=True, exist_ok=True)
            (self.root / "models" / setup.RECIPE / part / "config.json").write_text('{"bundled": true}')
        self.tests = []
        self.cancel = threading.Event()

    def fake_test(self, app, data, operation, payload, cancel, progress, log_id=None):
        self.tests.append((operation, payload))
        return {"gpu": "Test GPU", "seconds": 1.0}

    def add(self, path, name="", inference=None):
        with patch.object(setup, "inference", inference or self.fake_test):
            return models.import_model(self.app, {"dataRoot": str(self.root), "path": str(path), "name": name},
                                       self.cancel, lambda _: None)

    def listed(self):
        return {item["id"]: item for item in models.listing(self.app, {"dataRoot": str(self.root)})["models"]}

    def test_checkpoint_is_copied_hashed_tested_and_listed_ready(self):
        source = checkpoint(self.folder / "downloads" / "dreamy.safetensors")
        before = digest(source)
        added = self.add(source, "  Dreamy\tstyle  ")
        self.assertEqual((added["name"], added["status"], added["format"]), ("Dreamy style", "ready", "single-file"))
        copy = self.root / "models/imported" / added["id"] / "model.safetensors"
        record = json.loads((copy.parent / "model.json").read_text())
        self.assertEqual(record["files"], [{"path": "model.safetensors", "size": source.stat().st_size, "sha256": before}])
        self.assertEqual((digest(copy), digest(source)), (before, before))  # The original is untouched.
        self.assertEqual(self.tests, [("verify-model", {"model": added["id"], "modelFormat": "single-file", "ownVae": True, "runtime": "fp32-offload"})])
        self.assertEqual(record["runtime"], "fp32-offload")
        listed = self.listed()
        self.assertTrue(listed[models.BUILT_IN]["builtIn"])
        self.assertEqual(listed[added["id"]]["status"], "ready")

    def test_diffusers_folder_copies_weights_with_bundled_configs(self):
        source = diffusers_folder(self.folder / "downloads" / "folder-model")
        (source / "unet/config.json").write_text('{"user": "config is never used"}')
        added = self.add(source)
        copy = self.root / "models/imported" / added["id"]
        self.assertEqual(json.loads((copy / "unet/config.json").read_text()), {"bundled": True})
        self.assertEqual(digest(copy / "unet/diffusion_pytorch_model.safetensors"), digest(source / "unet/diffusion_pytorch_model.fp16.safetensors"))
        self.assertEqual(self.listed()[added["id"]]["status"], "ready")

    def test_failed_gpu_test_keeps_nothing(self):
        def fails(*_args, **_kwargs):
            raise setup.SetupError("Inference produced an empty or invalid image")
        with self.assertRaises(setup.SetupError):
            self.add(checkpoint(self.folder / "broken.safetensors"), inference=fails)
        self.assertEqual(list((self.root / "models/imported").iterdir()), [])
        self.assertEqual(list(self.listed()), [models.BUILT_IN, "sdxl-base"])

    def test_cancelled_copy_keeps_nothing(self):
        self.cancel.set()
        with self.assertRaises(setup.Cancelled):
            self.add(checkpoint(self.folder / "cancelled.safetensors"))
        self.assertEqual(list((self.root / "models/imported").iterdir()), [])

    def test_import_requires_the_verified_bundled_installation(self):
        setup.state_path(self.root).unlink()
        with self.assertRaises(setup.SetupError) as raised:
            self.add(checkpoint(self.folder / "early.safetensors"))
        self.assertIn("Install / verify Stable Diffusion 1.5", str(raised.exception))
        self.assertEqual(self.tests, [])

    def test_readiness_needs_its_own_test_and_the_bundled_installation(self):
        added = self.add(checkpoint(self.folder / "watched.safetensors"))
        copy = self.root / "models/imported" / added["id"] / "model.safetensors"
        with copy.open("r+b") as stream:
            stream.seek(-1, 2)
            stream.write(b"\1")
        self.assertEqual(self.listed()[added["id"]]["status"], "needs_verification")
        with self.assertRaises(setup.SetupError):
            models.ready_model(self.app, self.root, added["id"])
        with patch.object(setup, "inference", self.fake_test), self.assertRaises(setup.SetupError):
            models.retest(self.app, {"dataRoot": str(self.root), "modelId": added["id"]}, self.cancel, lambda _: None)
        self.assertEqual(self.listed()[added["id"]]["status"], "failed")  # The damaged copy fails native verification.

        other = self.add(checkpoint(self.folder / "other.safetensors", salt=b"other"))
        with patch.object(models, "IMPORT_ADAPTER_VERSION", 99):
            self.assertEqual(self.listed()[other["id"]]["status"], "needs_verification")
        runtime = self.root / "runtimes" / setup.PROFILE / "python" / "python.exe"
        runtime.unlink()
        self.assertEqual(self.listed()[other["id"]]["status"], "needs_verification")
        runtime.write_bytes(b"")
        self.assertEqual(self.listed()[other["id"]]["status"], "ready")
        with patch.object(setup, "inference", self.fake_test):
            self.assertEqual(models.retest(self.app, {"dataRoot": str(self.root), "modelId": other["id"]}, self.cancel, lambda _: None)["status"], "ready")

    def test_duplicates_and_names(self):
        source = checkpoint(self.folder / "same.safetensors")
        first = self.add(source, "Stable Diffusion 1.5")
        self.assertEqual(first["name"], "Stable Diffusion 1.5 (2)")
        with self.assertRaises(models.Refused) as raised:
            self.add(source)
        self.assertIn("already added as “Stable Diffusion 1.5 (2)”", str(raised.exception))
        self.assertEqual(len(list((self.root / "models/imported").iterdir())), 1)

    def test_interrupted_first_import_is_removed_and_ready_models_kept(self):
        kept = self.add(checkpoint(self.folder / "kept.safetensors"))
        stale = self.root / "models/imported" / ("c" * 32)
        stale.mkdir()
        (stale / "model.safetensors.partial").write_bytes(b"half")
        setup.atomic_json(stale / "model.json", {"id": "c" * 32, "name": "Half", "status": "copying", "format": "single-file", "files": []})
        orphan = self.root / "models/imported" / ("d" * 32)
        orphan.mkdir()
        listed = self.listed()
        self.assertFalse(stale.exists() or orphan.exists())
        self.assertEqual(listed[kept["id"]]["status"], "ready")

    def test_generation_names_a_ready_model_and_records_it(self):
        added = self.add(checkpoint(self.folder / "maker.safetensors"), "Maker")
        children = []
        def generation(app, data, operation, payload, cancel, progress, log_id=None):
            children.append(payload)
            return fake_generation(app, data, operation, payload, cancel, progress, log_id)
        payload = {"dataRoot": str(self.root), "prompt": "a lighthouse", "model": added["id"]}
        with patch.object(setup, "inference", generation):
            result = setup.generate(self.app, payload, self.cancel, lambda _: None)
        self.assertEqual((result["model"], result["modelName"], result["settings"]["model"]), (added["id"], "Maker", added["id"]))
        self.assertEqual(result["source"]["provider"], "local")
        self.assertEqual((children[0]["model"], children[0]["modelFormat"], children[0]["ownVae"]), (added["id"], "single-file", True))
        with patch.object(setup, "inference", generation):
            bundled = setup.generate(self.app, dict(payload, model=""), self.cancel, lambda _: None)
        self.assertEqual((bundled["settings"]["model"], bundled["modelName"]), (setup.RECIPE, "Stable Diffusion 1.5"))
        self.assertNotIn("modelFormat", children[1])
        with self.assertRaises(ValueError):
            setup.generate(self.app, dict(payload, model="../escape"), self.cancel, lambda _: None)

    def test_generation_with_an_unready_or_removed_model_writes_no_record(self):
        added = self.add(checkpoint(self.folder / "gone.safetensors"))
        models.remove(self.app, {"dataRoot": str(self.root), "modelId": added["id"]})
        jobs = set((self.root / "jobs").glob("*.json"))
        with patch.object(setup, "inference", fake_generation), self.assertRaises(setup.SetupError) as raised:
            setup.generate(self.app, {"dataRoot": str(self.root), "prompt": "x", "model": added["id"]}, self.cancel, lambda _: None)
        self.assertIn("no longer in the data folder", str(raised.exception))
        self.assertEqual(set((self.root / "jobs").glob("*.json")), jobs)

    def test_removal_keeps_images_jobs_and_the_bundled_model(self):
        added = self.add(checkpoint(self.folder / "removable.safetensors"))
        with patch.object(setup, "inference", fake_generation):
            image = setup.generate(self.app, {"dataRoot": str(self.root), "prompt": "x", "model": added["id"]}, self.cancel, lambda _: None)
        summary = storage.summary({"dataRoot": str(self.root)}, app=ROOT)
        self.assertGreater({item["id"]: item["bytes"] for item in summary["items"]}["imported"], 70 * 1024**2)
        result = models.remove(self.app, {"dataRoot": str(self.root), "modelId": added["id"]})
        self.assertEqual([item["id"] for item in result["models"]], [models.BUILT_IN, "sdxl-base"])
        self.assertTrue(Path(image["imagePath"]).is_file() and (self.root / "jobs" / (image["id"] + ".json")).is_file())
        self.assertEqual(result["models"][0]["status"], "ready")
        with self.assertRaises(ValueError):
            models.remove(self.app, {"dataRoot": str(self.root), "modelId": added["id"]})
        with self.assertRaises(ValueError):
            models.remove(self.app, {"dataRoot": str(self.root), "modelId": "../" + "a" * 29})


if __name__ == "__main__":
    unittest.main()
