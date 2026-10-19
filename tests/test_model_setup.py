"""Combined Models preflight, without downloading software, loading models or certifying a GPU."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_setup as setup
import model_setup
import sdxl

GTX = dict(name="NVIDIA GeForce GTX 1660 Ti", vramMiB=6144, computeCapability="7.5")


class ModelSetupTests(unittest.TestCase):
    def plans(self, root, ready=False, free=100 * 1024**3, ram=32768, gpu=GTX, xl_ready=False):
        with patch("model_setup.hardware.detect", return_value=dict(gpu=gpu, ramMiB=ram)), \
             patch("model_setup.addon.base_ready", return_value=ready), \
             patch("model_setup.sdxl.entry", return_value={"status": "ready" if xl_ready else "not_installed"}), \
             patch("model_setup.shutil.disk_usage", return_value=type("Disk", (), {"free": free})()):
            return model_setup.plans(ROOT, {"dataRoot": str(root)})["plans"]

    def test_first_xl_get_includes_all_prerequisites_and_leaves_no_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            base, xl = self.plans(root)
            xl_bytes = sdxl.XL.missing_bytes(ROOT, root)
            self.assertTrue(xl["requiresBase"])
            self.assertEqual(xl["downloadBytes"], base["downloadBytes"] + xl_bytes)
            self.assertEqual(xl["requiredBytes"], setup.FRESH_SETUP_BYTES + xl_bytes + setup.SETUP_HEADROOM_BYTES)
            self.assertEqual(list(root.iterdir()), [])
            self.assertFalse(base["ready"] or xl["ready"])

    def test_combined_space_is_refused_even_when_base_alone_fits(self):
        with tempfile.TemporaryDirectory() as folder:
            base, xl = self.plans(Path(folder), free=setup.FRESH_SETUP_BYTES)
            self.assertEqual(base["blocker"], "")
            self.assertIn("Free up space", xl["blocker"])

    def test_verified_shared_setup_is_reused_and_present_files_reduce_estimate(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            base, xl = self.plans(root, ready=True)
            self.assertEqual((base["downloadBytes"], base["requiredBytes"]), (0, 0))
            self.assertFalse(xl["requiresBase"])
            self.assertEqual(xl["requiredBytes"], xl["downloadBytes"] + setup.SETUP_HEADROOM_BYTES)
            _, path, entry = sdxl.XL.model_files(ROOT, root)[0]
            path.parent.mkdir(parents=True)
            path.write_bytes(b"x" * entry["size"])
            after = self.plans(root, ready=True)[1]
            self.assertEqual(after["downloadBytes"], xl["downloadBytes"] - entry["size"])
            self.assertFalse(after["ready"], "File presence is not verification")

    def test_xl_specific_memory_requirement_is_checked_before_base_downloads(self):
        with tempfile.TemporaryDirectory() as folder:
            base, xl = self.plans(Path(folder), ram=8192)
            self.assertEqual(base["blocker"], "")
            self.assertIn("16 GB", xl["blocker"])

    def test_incompatible_hardware_does_not_become_ready_from_old_evidence(self):
        with tempfile.TemporaryDirectory() as folder:
            base, xl = self.plans(Path(folder), ready=True, xl_ready=True, gpu=None)
            self.assertFalse(base["ready"] or xl["ready"])
            self.assertIn("NVIDIA", xl["blocker"])

    def test_invalid_folder_is_rejected_before_hardware_work(self):
        with patch("model_setup.hardware.detect") as detect:
            with self.assertRaises(ValueError):
                model_setup.plans(ROOT, {"dataRoot": "relative"})
            detect.assert_not_called()
