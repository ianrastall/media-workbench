"""Hardware profile and runtime choice from fake hardware descriptions; no GPU or ML runtime is needed."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import hardware  # noqa: E402
import inference_setup as setup  # noqa: E402
import storage  # noqa: E402
from test_inference_setup import fake_app, fake_generation, ready_install  # noqa: E402

MINIMUM = 6144


def machine(name, vram, capability="8.6", ram=32768, driver="610.60"):
    gpu = dict(name=name, vramMiB=vram, freeVramMiB=vram - 400, computeCapability=capability, driver=driver)
    return dict(gpu=gpu, gpuProblem=None, ramMiB=ram, availableRamMiB=ram // 2,
                drive=dict(path="D:\\data", freeBytes=100 * 1024**3, totalBytes=1000 * 1024**3, kind="ssd"))


class ChoiceTests(unittest.TestCase):
    def runtime(self, *args, **kwargs):
        return hardware.choose(machine(*args, **kwargs), MINIMUM)["runtime"]

    def test_gtx_16_series_keeps_the_conservative_profile(self):
        # The development machine: half precision makes blank pictures on these cards.
        choice = hardware.choose(machine("NVIDIA GeForce GTX 1660 Ti", 6144, "7.5", ram=65198), MINIMUM)
        self.assertEqual(choice["runtime"], hardware.CONSERVATIVE)
        self.assertIn("blank pictures at half precision", choice["reasons"][0])
        self.assertEqual(choice["warnings"], [])
        for name in ["NVIDIA GeForce GTX 1650 SUPER", "NVIDIA GeForce GTX 1660 Ti with Max-Q Design", "Quadro T1000"]:
            self.assertEqual(self.runtime(name, 8192, "7.5"), "fp32-offload", name)

    def test_rtx_cards_use_half_precision_and_offload_only_when_needed(self):
        self.assertEqual(self.runtime("NVIDIA GeForce RTX 2060", 6144, "7.5"), "fp16-offload")
        self.assertEqual(self.runtime("NVIDIA GeForce RTX 3050", 6144, "8.6"), "fp16-offload")
        self.assertEqual(self.runtime("NVIDIA GeForce RTX 4060", 8188, "8.9"), "fp16-gpu")  # Reported just under 8 GiB.
        self.assertEqual(self.runtime("NVIDIA GeForce RTX 3060", 12288, "8.6"), "fp16-gpu")
        self.assertEqual(self.runtime("Tesla T4", 15360, "7.5"), "fp16-gpu")
        choice = hardware.choose(machine("NVIDIA GeForce RTX 3060", 12288), MINIMUM)
        self.assertIn("holds the whole model", choice["reasons"][0])

    def test_older_cards_use_full_precision_on_the_gpu_when_it_fits(self):
        self.assertEqual(self.runtime("NVIDIA GeForce GTX 1080 Ti", 11264, "6.1"), "fp32-gpu")
        choice = hardware.choose(machine("NVIDIA GeForce GTX 1070", 8192, "6.1"), MINIMUM)
        self.assertEqual(choice["runtime"], "fp32-offload")
        self.assertIn("too old for fast half precision", choice["reasons"][0])

    def test_unknown_capability_from_an_old_driver_is_conservative(self):
        choice = hardware.choose(machine("NVIDIA GeForce RTX 3090", 24576, ""), MINIMUM)
        self.assertEqual(choice["runtime"], "fp32-gpu")
        self.assertIn("Updating the driver", choice["reasons"][0])
        self.assertEqual(self.runtime("NVIDIA GeForce RTX 3060", 6144, ""), "fp32-offload")

    def test_too_little_graphics_or_system_memory_is_refused_in_plain_words(self):
        small = hardware.choose(machine("NVIDIA GeForce GTX 1650", 4096, "7.5"), MINIMUM)
        self.assertIsNone(small["runtime"])
        self.assertIn("4 GB of graphics memory", small["blocked"])
        self.assertIn("at least 6 GB", small["blocked"])
        low_ram = hardware.choose(machine("NVIDIA GeForce RTX 3060", 12288, ram=4000), MINIMUM)
        self.assertIsNone(low_ram["runtime"])
        self.assertIn("at least 8 GB", low_ram["blocked"])
        missing = hardware.choose(dict(gpu=None, gpuProblem=None, ramMiB=16384), MINIMUM)
        self.assertIn("No NVIDIA graphics card", missing["blocked"])
        reported = hardware.choose(dict(gpu=None, gpuProblem="nvidia-smi failed; check the NVIDIA driver.", ramMiB=16384), MINIMUM)
        self.assertEqual(reported["blocked"], "nvidia-smi failed; check the NVIDIA driver.")

    def test_eight_gigabytes_of_memory_warns_only_when_the_model_waits_in_it(self):
        # 8 GB of memory as Windows usually reports it.
        offloaded = hardware.choose(machine("NVIDIA GeForce GTX 1660", 6144, "7.5", ram=7987), MINIMUM)
        self.assertEqual(offloaded["runtime"], "fp32-offload")
        self.assertEqual(len(offloaded["warnings"]), 1)
        self.assertIn("16 GB or more is recommended", offloaded["warnings"][0])
        resident = hardware.choose(machine("NVIDIA GeForce RTX 3060", 12288, ram=7987), MINIMUM)
        self.assertEqual((resident["runtime"], resident["warnings"]), ("fp16-gpu", []))
        unknown = hardware.choose(dict(machine("NVIDIA GeForce GTX 1660", 6144, "7.5"), ramMiB=None), MINIMUM)
        self.assertEqual((unknown["runtime"], unknown["warnings"]), ("fp32-offload", []))

    def test_the_child_refuses_settings_this_card_was_not_chosen_for(self):
        gtx = dict(name="NVIDIA GeForce GTX 1660 Ti", computeCapability="7.5", vramMiB=6143.7)
        self.assertTrue(hardware.allows("fp32-offload", gtx))
        for runtime in ["fp16-offload", "fp16-gpu", "fp32-gpu", "unknown", None]:
            self.assertFalse(hardware.allows(runtime, gtx), runtime)
        rtx = dict(name="NVIDIA GeForce RTX 2060", computeCapability="7.5", vramMiB=6143.7)
        self.assertTrue(hardware.allows("fp16-offload", rtx))
        self.assertFalse(hardware.allows("fp16-gpu", rtx))
        self.assertFalse(hardware.allows("fp32-offload", None))

    def test_every_runtime_is_described_and_the_conservative_one_is_last(self):
        self.assertEqual(list(hardware.RUNTIMES)[-1], hardware.CONSERVATIVE)
        for settings in hardware.RUNTIMES.values():
            self.assertIn(settings["precision"], ("float16", "float32"))
            self.assertTrue(settings["label"] and settings["description"])
            self.assertGreaterEqual(settings["minimumVramMiB"], MINIMUM)

    def test_upscaler_tile_follows_free_graphics_memory(self):
        self.assertEqual([hardware.upscale_tile(free) for free in (20000, 5120, 4000, 2000, 1000, 0)], [512, 512, 384, 256, 128, 128])

    def test_sizes_are_said_plainly(self):
        self.assertEqual([hardware.gib(value) for value in (6144, 6143.7, 7987, 12288, 65198, 4000)],
                         ["6 GB", "6 GB", "7.8 GB", "12 GB", "64 GB", "3.9 GB"])


class DetectionTests(unittest.TestCase):
    def test_old_drivers_are_asked_without_compute_capability(self):
        calls = []
        def run(arguments, **_):
            calls.append(arguments[1])
            if "compute_cap" in arguments[1]:
                return subprocess.CompletedProcess(arguments, 2, stdout='Field "compute_cap" is not a valid field to query.', stderr="")
            return subprocess.CompletedProcess(arguments, 0, stdout="0, GPU-aaaa, NVIDIA GeForce RTX 3060, 12288, 11000, 470.82\n", stderr="")
        environment = {k: v for k, v in os.environ.items() if k != "CUDA_VISIBLE_DEVICES"}
        with patch.object(hardware.os, "name", "nt"), patch.object(hardware.shutil, "which", return_value="nvidia-smi"), \
             patch.object(hardware.subprocess, "run", side_effect=run), patch.dict(os.environ, environment, clear=True):
            gpu = hardware.query_gpu()
        self.assertEqual(len(calls), 2)
        self.assertEqual((gpu["name"], gpu["vramMiB"], gpu["computeCapability"], gpu["driver"]), ("NVIDIA GeForce RTX 3060", 12288, "", "470.82"))

    def test_missing_driver_is_a_reason_not_a_crash(self):
        with patch.object(hardware.shutil, "which", return_value=None):
            found = hardware.detect(tempfile.gettempdir())
        self.assertIsNone(found["gpu"])
        self.assertIn("No NVIDIA graphics card", found["gpuProblem"])
        self.assertIn("NVIDIA", hardware.choose(found, MINIMUM)["blocked"])

    def test_drive_of_a_folder_that_does_not_exist_yet_is_read_without_creating_it(self):
        with tempfile.TemporaryDirectory() as folder:
            planned = Path(folder) / "not" / "yet"
            drive = hardware.drive_info(planned)
            self.assertFalse(planned.exists())
            self.assertEqual(Path(drive["path"]), planned)
            self.assertGreater(drive["freeBytes"], 0)
            self.assertIn(drive["kind"], ("ssd", "hdd", "usb", "removable", "network", "unknown"))

    @unittest.skipUnless(os.name == "nt", "Windows reports memory and drive kinds")
    def test_this_computer_reports_memory_and_drive(self):
        total, available = hardware.system_memory()
        self.assertGreater(total, 0)
        self.assertGreater(available, 0)
        self.assertGreater(hardware.drive_info(tempfile.gettempdir())["totalBytes"], 0)

    def test_drive_information_uses_the_same_rust_service_as_storage(self):
        with tempfile.TemporaryDirectory(prefix="MediaWorkbench-café-森林-") as folder:
            native = storage.drive_info(ROOT, folder)
            drive = hardware.drive_info(folder)
            self.assertEqual(drive["kind"], native["driveKind"])
            self.assertEqual(drive["totalBytes"], native["totalBytes"])
            self.assertGreater(drive["freeBytes"], 0)

    def test_missing_or_incompatible_native_core_never_uses_a_second_drive_implementation(self):
        from unittest.mock import Mock
        with patch.object(storage, "native_core_path", return_value=None), self.assertRaisesRegex(setup.SetupError, "missing"):
            hardware.drive_info(tempfile.gettempdir())
        for core in (Mock(mw_abi_version=Mock(return_value=99)), Mock(spec=["mw_abi_version"], mw_abi_version=Mock(return_value=1))):
            with patch.object(storage.ctypes, "CDLL", return_value=core), self.assertRaisesRegex(setup.SetupError, "incompatible"):
                hardware.drive_info(tempfile.gettempdir())

    def test_drive_information_refuses_invalid_paths_without_creating_them(self):
        for root in ("relative/data", Path(tempfile.gettempdir()).anchor):
            with self.subTest(root=root), self.assertRaisesRegex(ValueError, "absolute data folder"):
                hardware.drive_info(root)


class VerifiedRuntimeTests(unittest.TestCase):
    RTX = dict(name="NVIDIA GeForce RTX 3060", driver="610.60")

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.app, self.root = fake_app(self.folder), self.folder / "data"
        self.calls = []

    def inference(self, failing=()):
        def run(app, root, operation, payload, cancel, progress, log_id=None):
            self.calls.append((operation, payload.get("runtime")))
            if payload.get("runtime") in failing:
                raise setup.SetupError("Inference produced an empty or invalid image; the installation is not verified.")
            return {"gpu": self.RTX["name"], "seconds": 1.0}
        return run

    def test_each_runtime_has_its_own_readiness_stamp(self):
        stamps = {setup.profile_stamp(self.app, runtime) for runtime in hardware.RUNTIMES}
        self.assertEqual(len(stamps), len(hardware.RUNTIMES))
        ready = dict(status="ready", runtime="fp16-gpu", profileStamp=setup.profile_stamp(self.app, "fp16-gpu"))
        self.assertTrue(setup.stamp_valid(self.app, ready))
        self.assertFalse(setup.stamp_valid(self.app, dict(ready, runtime="fp32-offload")))
        self.assertFalse(setup.stamp_valid(self.app, dict(ready, runtime=None)))
        with patch.object(setup, "ADAPTER_VERSION", setup.ADAPTER_VERSION + 1):
            self.assertFalse(setup.stamp_valid(self.app, ready))

    def test_faster_runtime_that_fails_falls_back_and_is_not_retried_on_the_same_card(self):
        with patch.object(setup, "inference", self.inference(failing={"fp16-gpu"})):
            runtime, evidence, declined = setup.verify_runtime(self.app, self.root, self.RTX, "fp16-gpu", [], threading.Event(), lambda _: None)
        self.assertEqual((runtime, evidence["runtime"]), ("fp32-offload", "fp32-offload"))
        self.assertEqual(self.calls, [("verify", "fp16-gpu"), ("verify", "fp32-offload")])
        self.assertEqual([(item["runtime"], item["gpu"], item["driver"]) for item in declined], [("fp16-gpu", self.RTX["name"], "610.60")])
        self.calls.clear()
        with patch.object(setup, "inference", self.inference()):
            runtime, _, again = setup.verify_runtime(self.app, self.root, self.RTX, "fp16-gpu", declined, threading.Event(), lambda _: None)
        self.assertEqual((runtime, self.calls), ("fp32-offload", [("verify", "fp32-offload")]))
        self.assertEqual(again, declined)
        # A driver update gives the faster settings another chance.
        self.calls.clear()
        with patch.object(setup, "inference", self.inference()):
            runtime, _, _ = setup.verify_runtime(self.app, self.root, dict(self.RTX, driver="620.00"), "fp16-gpu", declined, threading.Event(), lambda _: None)
        self.assertEqual((runtime, self.calls), ("fp16-gpu", [("verify", "fp16-gpu")]))

    def test_conservative_failure_and_cancellation_are_never_ready(self):
        with patch.object(setup, "inference", self.inference(failing=set(hardware.RUNTIMES))), self.assertRaises(setup.SetupError):
            setup.verify_runtime(self.app, self.root, self.RTX, "fp16-gpu", [], threading.Event(), lambda _: None)
        self.assertEqual(self.calls, [("verify", "fp16-gpu"), ("verify", "fp32-offload")])
        def cancelled(*_args, **_kwargs):
            raise setup.Cancelled("Operation cancelled.")
        with patch.object(setup, "inference", cancelled), self.assertRaises(setup.Cancelled):
            setup.verify_runtime(self.app, self.root, self.RTX, "fp16-gpu", [], threading.Event(), lambda _: None)

    def test_blocked_hardware_stops_setup_with_the_plain_reason(self):
        small = machine("NVIDIA GeForce GTX 1650", 4096, "7.5")
        with patch.object(hardware, "detect", return_value=small), self.assertRaisesRegex(setup.SetupError, "4 GB of graphics memory"):
            setup.check_hardware(self.app, self.root)
        with patch.object(hardware, "detect", return_value=machine("NVIDIA GeForce RTX 3060", 12288)):
            _, choice = setup.check_hardware(self.app, self.root)
        self.assertEqual(choice["runtime"], "fp16-gpu")

    def test_generation_runs_with_the_verified_runtime_and_records_it(self):
        ready_install(self.app, self.root, "fp16-offload")
        sent = []
        def generation(app, data, operation, payload, cancel, progress, log_id=None):
            sent.append(payload.get("runtime"))
            return fake_generation(app, data, operation, payload, cancel, progress, log_id)
        with patch.object(setup, "inference", side_effect=generation):
            record = setup.generate(self.app, {"dataRoot": str(self.root), "prompt": "house"}, threading.Event(), lambda _: None)
        self.assertEqual((sent, record["runtime"]), (["fp16-offload"], "fp16-offload"))
        self.assertEqual(setup.status(self.app, {"dataRoot": str(self.root)})["status"], "ready")

    def test_profile_says_when_faster_settings_are_available_or_the_card_changed(self):
        ready_install(self.app, self.root)
        payload = {"dataRoot": str(self.root)}
        with patch.object(hardware, "detect", return_value=machine("NVIDIA GeForce GTX 1660 Ti", 6144, "7.5")):
            profile = setup.system_profile(self.app, payload)
        self.assertEqual((profile["recommended"]["runtime"], profile["verified"]["id"], profile["note"]), ("fp32-offload", "fp32-offload", ""))
        self.assertEqual(profile["recommended"]["settings"]["label"], "Memory-saving")
        with patch.object(hardware, "detect", return_value=machine("NVIDIA GeForce RTX 3060", 12288)):
            profile = setup.system_profile(self.app, payload)
        self.assertIn("can use fast settings", profile["note"])
        record = json.loads(setup.state_path(self.root).read_text())
        record["declinedRuntimes"] = [dict(runtime="fp16-gpu", gpu="NVIDIA GeForce RTX 3060", driver="610.60", adapter=setup.ADAPTER_VERSION)]
        setup.atomic_json(setup.state_path(self.root), record)
        with patch.object(hardware, "detect", return_value=machine("NVIDIA GeForce RTX 3060", 12288)):
            self.assertEqual(setup.system_profile(self.app, payload)["note"], "")  # It already failed its test here.
        ready_install(self.app, self.root, "fp16-gpu")
        with patch.object(hardware, "detect", return_value=machine("NVIDIA GeForce GTX 1660 Ti", 6144, "7.5")):
            changed = setup.system_profile(self.app, payload)
        self.assertIn("graphics card changed", changed["note"])
        self.assertIsNone(changed["verified"])
        unread = dict(machine("NVIDIA GeForce RTX 3060", 12288), gpu=None, gpuProblem="nvidia-smi could not be run")
        with patch.object(hardware, "detect", return_value=unread):
            profile = setup.system_profile(self.app, payload)
        # A driver that cannot be read right now is reported as such, not as a changed card.
        self.assertEqual((profile["note"], profile["verified"]["id"], profile["recommended"]["blocked"]), ("", "fp16-gpu", "nvidia-smi could not be run"))

    def test_profile_does_not_describe_a_missing_or_changed_installation_as_verified(self):
        for missing in ("runtime", "model", "changed-model"):
            with self.subTest(missing=missing):
                ready_install(self.app, self.root)
                target = (self.root / f"runtimes/{setup.PROFILE}/python/python.exe" if missing == "runtime"
                          else self.root / f"models/{setup.RECIPE}/unet/weights.bin")
                if missing == "changed-model":
                    target.write_bytes(b"changed")
                else:
                    target.unlink()
                before = setup.state_path(self.root).read_bytes()
                with patch.object(hardware, "detect", return_value=machine("NVIDIA GeForce GTX 1660 Ti", 6144, "7.5")):
                    self.assertIsNone(setup.system_profile(self.app, {"dataRoot": str(self.root)})["verified"])
                self.assertEqual(setup.state_path(self.root).read_bytes(), before)

    def test_profile_of_a_new_data_folder_creates_nothing(self):
        planned = self.folder / "planned"
        with patch.object(hardware, "detect", wraps=hardware.detect) as detect, \
             patch.object(storage, "native_core_path", return_value=setup.native_core_path(ROOT)), \
             patch.object(hardware, "query_gpu", side_effect=hardware.HardwareError("No NVIDIA graphics card was found.")):
            profile = setup.system_profile(self.app, {"dataRoot": str(planned)})
        detect.assert_called_once()
        self.assertFalse(planned.exists())
        self.assertIsNone(profile["verified"])
        self.assertEqual(profile["recommended"]["blocked"], "No NVIDIA graphics card was found.")
        json.dumps(profile)  # Sent to the GUI as JSON.


if __name__ == "__main__":
    unittest.main()
