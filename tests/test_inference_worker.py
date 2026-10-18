"""Torch-free checks of the ML child's own logic, with stand-ins for the pipeline parts."""
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_worker as worker  # noqa: E402


class Part:
    """A pipeline part that records where it is moved and lets hooks be registered on it."""

    def __init__(self, device):
        self.device, self.moves, self.hooks = device, [], []

    def parameters(self):
        yield type("Parameter", (), {"device": type("Device", (), {"type": self.device})()})()

    def to(self, device):
        self.moves.append(device)
        self.device = device
        return self

    def register_forward_pre_hook(self, hook):
        self.hooks.append(hook)


class VaePlacementTests(unittest.TestCase):
    def test_the_vae_leaves_the_gpu_before_the_unet_runs(self):
        pipe = type("Pipe", (), {})()
        pipe.vae, pipe.unet = Part("cuda"), Part("cpu")
        worker.vae_off_while_denoising(pipe)
        self.assertEqual(len(pipe.unet.hooks), 1)
        pipe.unet.hooks[0](pipe.unet, ())
        self.assertEqual(pipe.vae.moves, ["cpu"], "the encoder's VAE does not stay beside the UNet")
        pipe.unet.hooks[0](pipe.unet, ())
        self.assertEqual(pipe.vae.moves, ["cpu"], "a VAE already off the GPU is left alone on later steps")

    def test_pipelines_without_both_parts_are_left_alone(self):
        pipe = type("Pipe", (), {"vae": Part("cuda")})()
        worker.vae_off_while_denoising(pipe)
        self.assertEqual(pipe.vae.moves, [])


class ServeTests(unittest.TestCase):
    def test_only_pictures_are_served_and_errors_keep_the_loop_answering(self):
        requests = io.StringIO("\n".join([json.dumps({"operation": "upscale", "payload": {}}), "not json"]) + "\n")
        written = io.StringIO()
        with patch.object(worker, "WIRE_OUT", written), patch.object(sys, "stdin", requests):
            worker.serve(type("Args", (), {})())
        events = [json.loads(line) for line in written.getvalue().splitlines()]
        self.assertEqual([event["kind"] for event in events], ["error", "error"])
        self.assertIn("only makes pictures", events[0]["payload"]["message"])


if __name__ == "__main__":
    unittest.main()
