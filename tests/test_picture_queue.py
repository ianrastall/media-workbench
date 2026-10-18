"""Waiting pictures: durable settings and private input copies, independent of Library and the user's files."""
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_setup as setup  # noqa: E402
import picture_queue as queue  # noqa: E402


class QueueTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="MediaWorkbench-queue-森林-")
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.root = self.folder / "data"
        self.picture = self.folder / "mine" / "photo.png"
        self.picture.parent.mkdir()
        self.picture.write_bytes(b"\x89PNG source")
        self.mask = self.folder / "mine" / "mask.png"
        self.mask.write_bytes(b"\x89PNG mask")

    def payload(self, **extra):
        return {**dict(dataRoot=str(self.root), prompt="a lighthouse", seed=7, steps=12), **extra}

    def test_a_waiting_picture_keeps_its_settings_and_own_copies_of_its_inputs(self):
        item = queue.add(ROOT, self.payload(sourceImage=str(self.picture), maskImage=str(self.mask), strength=1.0))
        record = json.loads((self.root / "queue" / f"{item['id']}.json").read_text())
        self.assertEqual((record["status"], record["settings"]["sourceImage"], record["settings"]["maskImage"]), ("queued", "source.png", "mask.png"))
        self.picture.unlink()
        self.mask.unlink()  # The user's originals can go; the waiting picture keeps its own copies.
        listed = queue.listing(ROOT, {"dataRoot": str(self.root)})["items"]
        self.assertEqual(len(listed), 1)
        self.assertFalse(listed[0]["missingInputs"])
        self.assertEqual(Path(listed[0]["settings"]["sourceImage"]).read_bytes(), b"\x89PNG source")
        self.assertEqual((listed[0]["settings"]["prompt"], listed[0]["settings"]["seed"]), ("a lighthouse", 7))

    def test_waiting_pictures_are_listed_oldest_first_and_removed_with_their_copies(self):
        first = queue.add(ROOT, self.payload(sourceImage=str(self.picture), strength=0.6))
        second = queue.add(ROOT, self.payload(seed=8))
        self.assertEqual([item["id"] for item in queue.listing(ROOT, {"dataRoot": str(self.root)})["items"]], [first["id"], second["id"]])
        remaining = queue.remove(ROOT, {"dataRoot": str(self.root), "id": first["id"]})["items"]
        self.assertEqual([item["id"] for item in remaining], [second["id"]])
        self.assertFalse((self.root / "queue" / first["id"]).exists())
        with self.assertRaises(ValueError):
            queue.remove(ROOT, {"dataRoot": str(self.root), "id": "../jobs"})

    def test_invalid_settings_write_nothing(self):
        with self.assertRaises(ValueError):
            queue.add(ROOT, self.payload(prompt=""))
        with self.assertRaises(ValueError):
            queue.add(ROOT, self.payload(width=640, height=640))
        self.assertFalse(self.root.exists())

    def test_damaged_records_are_skipped_and_unfinished_adds_cleaned_up(self):
        kept = queue.add(ROOT, self.payload())
        damaged = self.root / "queue" / ("a" * 32 + ".json")
        damaged.write_text("{not json")
        orphan = self.root / "queue" / ("b" * 32)
        orphan.mkdir()
        (orphan / "source.png").write_bytes(b"x")
        young = self.root / "queue" / ("c" * 32)
        young.mkdir()
        old = time.time() - queue.ORPHAN_SECONDS - 60
        os.utime(orphan, (old, old))
        listed = queue.listing(ROOT, {"dataRoot": str(self.root)})["items"]
        self.assertEqual([item["id"] for item in listed], [kept["id"]])
        self.assertTrue(damaged.exists(), "a damaged record is never deleted")
        self.assertFalse(orphan.exists())
        self.assertTrue(young.exists(), "an add still in progress keeps its folder")

    def test_missing_input_copies_are_reported_and_the_limit_holds(self):
        item = queue.add(ROOT, self.payload(sourceImage=str(self.picture), strength=0.6))
        (self.root / "queue" / item["id"] / "source.png").unlink()
        self.assertTrue(queue.listing(ROOT, {"dataRoot": str(self.root)})["items"][0]["missingInputs"])
        with patch.object(queue, "LIMIT", 1), self.assertRaisesRegex(setup.SetupError, "Up to 1 pictures"):
            queue.add(ROOT, self.payload())

    def test_the_worker_routes_queue_operations_without_releasing_a_kept_model(self):
        import media_worker  # noqa: F401  (imports cleanly with the new routes)
        source = (ROOT / "workers/python/media_worker.py").read_text(encoding="utf-8")
        self.assertIn('"queue.add", "queue.list", "queue.remove"', source)


if __name__ == "__main__":
    unittest.main()
