"""Paged Library behavior with real SQLite and temporary job records; no GPU or network."""
import json
from contextlib import closing
import os
from pathlib import Path
import shutil
import sqlite3
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
tempfile.tempdir = str(Path(tempfile.gettempdir()).resolve())
sys.path.insert(0, str(ROOT / "workers/python"))
import inference_setup as setup


def write_job(root, number, prompt="A red house", kind="generate", **changes):
    job_id = f"{number:032x}"
    image = root / "artifacts" / job_id / "image.png"
    image.parent.mkdir(parents=True, exist_ok=True)
    image.write_bytes(b"png")
    record = dict(id=job_id, status="succeeded", kind=kind, imagePath=f"{job_id}/image.png",
                  imageSha256="0" * 64, settings=dict(prompt=prompt, negativePrompt="blurry", seed=number, steps=10),
                  completedUtc="2026-10-17T12:00:00Z", seconds=1.2, gpu="Test GPU")
    record.update(changes)
    path = root / "jobs" / f"{job_id}.json"
    setup.atomic_json(path, record)
    # Fixed order, including exact ties: enumeration order must never decide page boundaries.
    os.utime(path, ns=(1_700_000_000_000_000_000, 1_700_000_000_000_000_000))
    return record


class LibraryIndexTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def list(self, **options):
        return setup.library(dict(dataRoot=str(self.root), **options))

    def test_pages_reach_every_image_beyond_the_old_limit_without_duplicates(self):
        for number in range(253):
            write_job(self.root, number)
        seen = []
        for offset in range(0, 253, 48):
            page = self.list(offset=offset, limit=48)
            seen.extend(image["id"] for image in page["artifacts"])
            self.assertEqual((page["total"], page["offset"], page["hasMore"]), (253, offset, offset + 48 < 253))
            self.assertLessEqual(len(page["artifacts"]), 48)
        self.assertEqual(seen, [f"{number:032x}" for number in reversed(range(253))])

    def test_search_is_casefolded_literal_and_filters_image_type(self):
        write_job(self.root, 42, "CAFÉ Straße 森林 100%_ ' OR 1=1 --")
        write_job(self.root, 43, "An enlarged forest", "upscale")
        for query in ("café", "STRASSE", "森林", "100%_", "' OR 1=1 --", "42", f"{42:032x}"):
            with self.subTest(query=query):
                self.assertEqual([image["id"] for image in self.list(query=query)["artifacts"]], [f"{42:032x}"])
        self.assertEqual(self.list(query="%_")["total"], 1)
        self.assertEqual(self.list(kind="upscale")["total"], 1)
        self.assertEqual(self.list(query="café", kind="upscale")["total"], 0)
        self.assertEqual(self.list(query="BLURRY", kind="generate")["total"], 1)

    def test_unchanged_records_are_not_decoded_from_disk_again(self):
        for number in range(5):
            write_job(self.root, number)
        self.list()
        with patch.object(setup, "read_record", wraps=setup.read_record) as read:
            self.list(query="house")
            self.assertEqual(read.call_count, 0)
            write_job(self.root, 3, "Changed prompt")
            self.assertEqual(self.list(query="changed")["total"], 1)
            self.assertEqual(read.call_count, 1)

    def test_external_deletions_and_reappearing_images_refresh_counts_and_pages(self):
        for number in range(4):
            write_job(self.root, number)
        self.assertEqual(self.list(limit=2, offset=2)["offset"], 2)
        image = self.root / "artifacts" / f"{3:032x}" / "image.png"
        image.unlink()
        (self.root / "jobs" / f"{2:032x}.json").unlink()
        page = self.list(limit=2, offset=2)
        self.assertEqual((page["total"], page["offset"], page["hasMore"]), (2, 0, False))
        image.write_bytes(b"png")
        self.assertEqual(self.list()["total"], 3)

    def test_index_follows_a_moved_data_folder_and_resolves_private_inputs(self):
        record = write_job(self.root, 1)
        source = self.root / "artifacts" / record["id"] / "source.jpg"
        source.write_bytes(b"jpg")
        record["settings"]["sourceImage"] = f"{record['id']}/source.jpg"
        record["imagePath"] = str(source.with_name("image.png"))  # Legacy absolute-path job.
        setup.atomic_json(self.root / "jobs" / f"{record['id']}.json", record)
        self.list()
        moved = self.root / "moved"
        moved.mkdir()
        for name in ("library", "jobs", "artifacts"):
            shutil.move(str(self.root / name), str(moved / name))
        result = setup.library({"dataRoot": str(moved)})["artifacts"][0]
        self.assertEqual(Path(result["imagePath"]), moved / "artifacts" / record["id"] / "image.png")
        self.assertEqual(Path(result["settings"]["sourceImage"]), moved / "artifacts" / record["id"] / "source.jpg")

    def test_corrupt_and_future_indexes_are_rebuilt_without_changing_records(self):
        record = write_job(self.root, 1)
        path = self.root / "jobs" / f"{record['id']}.json"
        original = path.read_bytes()
        self.list()
        index = self.root / "library" / "index.sqlite3"
        index.write_bytes(b"not a SQLite database")
        self.assertEqual(self.list()["total"], 1)
        with closing(sqlite3.connect(index)) as db:
            db.execute("PRAGMA user_version = 100")
        self.assertEqual(self.list()["total"], 1)
        index.unlink()
        self.assertEqual(self.list()["total"], 1)
        self.assertEqual(path.read_bytes(), original)

    def test_damage_and_invalid_field_types_do_not_block_good_records(self):
        write_job(self.root, 1)
        for number, changes in enumerate((dict(settings=[]), dict(seconds="broken"), dict(settings={"prompt": 7}),
                                          dict(settings={"steps": None}), dict(settings={"seed": 2**64}), dict(width=None), dict(status=[])), 2):
            write_job(self.root, number, **changes)
        path = self.root / "jobs" / f"{9:032x}.json"
        path.write_text("{damaged", encoding="utf-8")
        self.assertEqual(self.list()["total"], 1)
        write_job(self.root, 9, "Repaired record")
        self.assertEqual(self.list()["total"], 2)

    def test_load_timings_are_listed_and_damaged_ones_are_ignored(self):
        write_job(self.root, 1, loadSeconds=48.2, startupSeconds=5.5, loadNote="Loading the model took 48 seconds.")
        write_job(self.root, 2, "Older record without timings")
        for number, changes in enumerate((dict(loadSeconds="slow"), dict(startupSeconds=-1), dict(loadNote=12)), 3):
            write_job(self.root, number, **changes)
        listed = {image["id"]: image for image in self.list()["artifacts"]}
        self.assertEqual(len(listed), 2)
        self.assertEqual((listed[f"{1:032x}"]["loadSeconds"], listed[f"{1:032x}"]["loadNote"]), (48.2, "Loading the model took 48 seconds."))
        self.assertNotIn("loadSeconds", listed[f"{2:032x}"])

    def test_records_name_their_model_or_leave_it_unset(self):
        write_job(self.root, 1, settings=dict(prompt="older", seed=1, steps=10, model=None))
        write_job(self.root, 2, settings=dict(prompt="added", seed=2, steps=10, model="e" * 32), modelName="Dreamy")
        write_job(self.root, 3, settings=dict(prompt="bad", seed=3, steps=10, model=7))
        write_job(self.root, 4, modelName=["not", "a", "name"])
        listed = {image["settings"]["prompt"]: image for image in self.list()["artifacts"]}
        self.assertEqual(sorted(listed), ["added", "older"])
        self.assertEqual((listed["added"]["settings"]["model"], listed["added"]["modelName"]), ("e" * 32, "Dreamy"))

    def test_live_jobs_are_preserved_and_orphans_recovered_with_filters(self):
        record = write_job(self.root, 1, status="running", message="")
        with setup.exclusive(self.root):
            self.assertEqual(self.list(query="no matches")["recovered"], 0)
            self.assertEqual(json.loads((self.root / "jobs" / f"{record['id']}.json").read_text())["status"], "running")
        result = self.list(query="no matches", kind="upscale")
        self.assertEqual((result["total"], result["recovered"]), (0, 1))
        self.assertEqual(result["problems"][0]["status"], "interrupted")
        self.assertEqual(self.list()["recovered"], 0)

    def test_containment_is_rechecked_after_indexing(self):
        record = write_job(self.root, 1)
        self.assertEqual(self.list()["total"], 1)
        record["imagePath"] = f"{record['id']}/../../outside.png"
        (self.root / "outside.png").write_bytes(b"png")
        setup.atomic_json(self.root / "jobs" / f"{record['id']}.json", record)
        self.assertEqual(self.list()["total"], 0)

    def test_invalid_options_and_precancelled_reads_create_no_data(self):
        root = self.root / "untouched"
        for bad in (dict(offset=-1), dict(offset=True), dict(offset=2**63), dict(limit=0), dict(limit=201),
                    dict(kind="videos"), dict(query=None), dict(query="x" * 2001)):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                setup.library(dict(dataRoot=str(root), **bad))
        cancel = threading.Event()
        cancel.set()
        with self.assertRaises(setup.Cancelled):
            setup.library({"dataRoot": str(root)}, cancel)
        self.assertFalse(root.exists())

    def test_cancelled_refresh_rolls_back_and_releases_the_index(self):
        write_job(self.root, 1)
        self.list()
        write_job(self.root, 2)
        with patch.object(setup, "read_record", side_effect=setup.Cancelled("stopped")):
            with self.assertRaises(setup.Cancelled):
                self.list()
        self.assertEqual(self.list()["total"], 2)


if __name__ == "__main__":
    unittest.main()
