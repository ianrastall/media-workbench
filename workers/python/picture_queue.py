"""Pictures waiting to be made: durable settings and private input copies, run only when the user asks.

Standard library only. Each waiting picture is `queue/<id>.json` with its own copies of any starting
picture and painted area in `queue/<id>/`, written before the add is acknowledged, so the queue survives
closing the app or a crash and does not depend on the user's original files. Nothing here runs
inference: the desktop makes each picture through the ordinary generation path (which makes the job's own
private copies) and then removes the item. Library, clean-up and recovery never see waiting pictures.
"""
from datetime import datetime, timezone
from pathlib import Path
import shutil
import time
import uuid

import inference_setup as setup

LIMIT = 200
# A folder whose record was never written (the add stopped part-way) is removed after this long.
ORPHAN_SECONDS = 600


def folder(root):
    return setup.owned(root, "queue")


def record_path(root, item_id):
    if not setup.job_id_valid(item_id):
        raise ValueError("Choose one of the waiting pictures")
    return setup.owned(folder(root), f"{item_id}.json")


def inputs(root, item_id):
    return setup.owned(folder(root), item_id)


def add(app, payload):
    """Validates like a generation, copies its inputs, then writes the record that makes it a waiting picture."""
    settings = setup.validate_generation(payload)  # Before data_root, which creates the folder.
    root = setup.data_root(payload)
    queue = folder(root)
    queue.mkdir(exist_ok=True)
    if sum(1 for _ in queue.glob("*.json")) >= LIMIT:
        raise setup.SetupError(f"Up to {LIMIT} pictures can wait at once. Make or remove some first.")
    item_id = uuid.uuid4().hex
    own = inputs(root, item_id)
    try:
        for key, stem in (("sourceImage", "source"), ("maskImage", "mask")):
            if key in settings:
                own.mkdir(exist_ok=True)
                name = stem + Path(settings[key]).suffix.lower()
                shutil.copyfile(settings[key], setup.owned(own, name))
                settings[key] = name  # Stored relative to the item's own folder.
        record = dict(schemaVersion=1, id=item_id, status="queued", addedUtc=datetime.now(timezone.utc).isoformat(),
                      settings=settings)
        setup.atomic_json(record_path(root, item_id), record)
    except BaseException:
        shutil.rmtree(own, ignore_errors=True)
        raise
    return resolved(root, record)


def resolved(root, record):
    """The record for clients: input copies as absolute paths, or an item marked as missing them."""
    settings = dict(record["settings"])
    missing = False
    for key in ("sourceImage", "maskImage"):
        name = settings.get(key)
        if name is None:
            continue
        path = setup.owned(inputs(root, record["id"]), Path(name).name)
        settings[key] = str(path)
        missing = missing or not path.is_file()
    return dict(id=record["id"], addedUtc=record["addedUtc"], settings=settings, missingInputs=missing)


def listing(app, payload):
    """Waiting pictures, oldest first. Folders left by an add that never finished are removed."""
    root = setup.data_root(payload)
    queue = folder(root)
    if not queue.is_dir():
        return {"items": []}
    records = []
    for path in queue.glob("*.json"):
        record = setup.read_record(path)
        if not record or record.get("id") != path.stem or not setup.job_id_valid(path.stem) \
                or record.get("status") != "queued" or not isinstance(record.get("settings"), dict) \
                or not isinstance(record.get("addedUtc"), str):
            continue  # A damaged record is skipped, never deleted.
        records.append(record)
    known = {record["id"] for record in records}
    for leftover in queue.iterdir():
        if leftover.is_dir() and not leftover.is_symlink() and setup.job_id_valid(leftover.name) and leftover.name not in known \
                and time.time() - leftover.stat().st_mtime > ORPHAN_SECONDS:
            shutil.rmtree(leftover, ignore_errors=True)
    for stale in queue.glob("*.json.*.tmp"):
        if time.time() - stale.stat().st_mtime > ORPHAN_SECONDS:
            stale.unlink(missing_ok=True)
    records.sort(key=lambda record: (record["addedUtc"], record["id"]))
    return {"items": [resolved(root, record) for record in records]}


def remove(app, payload):
    """Removes a waiting picture: its record first, so it never runs again, then its input copies.
    Pictures already made from it are ordinary Library jobs with copies of their own, and are never touched."""
    root = setup.data_root(payload)
    item_id = payload.get("id")
    path = record_path(root, item_id)
    path.unlink(missing_ok=True)
    shutil.rmtree(inputs(root, item_id), ignore_errors=True)
    return listing(app, payload)
