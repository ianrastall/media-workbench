"""Behavioral protocol checks using a real subprocess; standard library only."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.worker = subprocess.Popen([sys.executable, "-u", str(ROOT / "workers/python/media_worker.py"),
                                       "--root", str(ROOT)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.assertEqual(self.read()["kind"], "hello")

    def tearDown(self):
        self.worker.stdin.close()
        try:
            self.worker.wait(timeout=8)
        except subprocess.TimeoutExpired:
            self.worker.kill()
            self.worker.wait(timeout=3)
        self.worker.stdout.close()
        self.worker.stderr.close()

    def read(self):
        return json.loads(self.worker.stdout.readline())

    def send(self, request):
        self.worker.stdin.write(json.dumps(request) + "\n")
        self.worker.stdin.flush()

    def test_non_object_request_is_rejected(self):
        self.send([1, 2])
        self.assertEqual(self.read()["payload"]["code"], "invalid_request")

    def test_protocol_mismatch_is_rejected(self):
        self.send({"protocol": 2, "id": "test", "operation": "health", "payload": {}})
        self.assertEqual(self.read()["payload"]["code"], "invalid_request")

    def test_generation_rejects_missing_prompt(self):
        self.send({"protocol": 1, "id": "test", "operation": "generate.image", "payload": {}})
        self.assertEqual(self.read()["payload"]["code"], "invalid_input")

    def test_output_requires_absolute_directory(self):
        self.send({"protocol": 1, "id": "test", "operation": "demo.run", "payload": {"outputDirectory": "relative"}})
        self.assertEqual(self.read()["payload"]["code"], "invalid_input")

    def test_cancel_does_not_write_an_artifact(self):
        with tempfile.TemporaryDirectory(prefix="media-workbench-check-") as folder:
            self.send({"protocol": 1, "id": "cancel", "operation": "demo.run",
                       "payload": {"outputDirectory": folder, "steps": 100, "delayMs": 100}})
            self.assertEqual(self.read()["kind"], "progress")
            self.send({"protocol": 1, "id": "cancel", "operation": "cancel", "payload": {}})
            event = self.read()
            while event["kind"] == "progress":
                event = self.read()
            self.assertEqual(event["payload"]["code"], "cancelled")
            self.assertEqual(list(Path(folder).iterdir()), [])


if __name__ == "__main__":
    unittest.main()
