"""Rebuildable SQLite view of job JSON. The records and pictures remain the source of truth.

Only changed records are decoded during refresh. Every refresh still stats job files and
checks image containment/existence, so external edits and moved data folders are reflected.
The index has its own lock; browsing never takes ownership from a live inference worker.
"""
import json
import sqlite3
import threading

import inference_setup as setup

VERSION = 1
KINDS = ("all", "generate", "upscale")


def options(payload):
    query = payload.get("query", "")
    kind = payload.get("kind", "all")
    offset = payload.get("offset", 0)
    limit = payload.get("limit", setup.LIBRARY_IMAGES)
    if not isinstance(query, str) or len(query) > 2000:
        raise ValueError("Library search must contain at most 2000 characters")
    if kind not in KINDS:
        raise ValueError("Choose all images, generated images, or upscaled images")
    if type(offset) is not int or not 0 <= offset <= 2_147_483_647:
        raise ValueError("Library offset must be a nonnegative integer")
    if type(limit) is not int or not 1 <= limit <= setup.LIBRARY_IMAGES:
        raise ValueError(f"Library limit must be from 1 to {setup.LIBRARY_IMAGES}")
    return query.strip().casefold(), kind, offset, limit


def usable_record(record, name):
    """Ignore damaged records, including well-formed JSON with unusable field types."""
    if not record or not setup.job_id_valid(record.get("id")) or name != record["id"] + ".json":
        return False
    if not isinstance(record.get("status"), str) or record["status"] not in {"running", "succeeded", *setup.PROBLEM_STATUSES}:
        return False
    settings = record.get("settings", {})
    if not isinstance(settings, dict):
        return False
    if settings.get("sourceImage") is not None and not isinstance(settings["sourceImage"], str):
        return False
    for key in ("prompt", "negativePrompt", "sampler"):
        if key in settings and not isinstance(settings[key], str):
            return False
    for key in ("seed", "steps", "width", "height", "scale"):
        if key in settings and (type(settings[key]) is not int or not 0 <= settings[key] <= (4294967295 if key == "seed" else 2147483647)):
            return False
    for key in ("guidanceScale", "strength"):
        if key in settings and (type(settings[key]) not in (int, float) or not 0 <= settings[key] <= 100):
            return False
    for key in ("startedUtc", "completedUtc", "imageSha256", "gpu", "kind"):
        if key in record and not isinstance(record[key], str):
            return False
    for key in ("width", "height"):
        if key in record and (type(record[key]) is not int or not 0 <= record[key] <= 2147483647):
            return False
    if "seconds" in record and (type(record["seconds"]) not in (int, float) or not 0 <= record["seconds"] < float("inf")):
        return False
    return True


def initialize(db):
    db.executescript("""
        CREATE TABLE IF NOT EXISTS jobs (
            name TEXT PRIMARY KEY, mtime INTEGER NOT NULL, size INTEGER NOT NULL,
            status TEXT NOT NULL, kind TEXT NOT NULL, search TEXT NOT NULL,
            image TEXT, available INTEGER NOT NULL, record TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS jobs_listing ON jobs(status, available, mtime DESC, name DESC);
        CREATE INDEX IF NOT EXISTS jobs_kind ON jobs(status, available, kind, mtime DESC, name DESC);
    """)
    db.execute(f"PRAGMA user_version = {VERSION}")


def synchronize(db, root, cancel):
    db.execute("BEGIN IMMEDIATE")
    db.execute("CREATE TEMP TABLE IF NOT EXISTS seen (name TEXT PRIMARY KEY)")
    db.execute("DELETE FROM seen")
    known = {row[0]: row[1:] for row in db.execute("SELECT name, mtime, size, status, image, available FROM jobs")}
    for path in setup.owned(root, "jobs").glob("*.json"):
        setup.check_cancel(cancel)
        if not setup.job_id_valid(path.stem):
            continue
        path = setup.owned(root, f"jobs/{path.name}")
        try:
            stat = path.stat()
        except FileNotFoundError:
            continue  # Atomic writer or an external removal; the next refresh sees the final file.
        db.execute("INSERT INTO seen VALUES (?)", (path.name,))
        prior = known.get(path.name)
        if prior is not None and prior[:2] == (stat.st_mtime_ns, stat.st_size):
            # Do not trust a cached absolute path, even after moving the entire folder.
            available = int(prior[2] == "succeeded" and setup.artifact_image(root, {"id": path.stem, "imagePath": prior[3]}) is not None)
            if available != prior[4]:
                db.execute("UPDATE jobs SET available = ? WHERE name = ?", (available, path.name))
            continue
        record = setup.read_record(path)
        if not usable_record(record, path.name):
            db.execute("DELETE FROM jobs WHERE name = ?", (path.name,))
            continue
        settings = record.get("settings", {})
        search = "\n".join(str(settings.get(key, "")) for key in ("prompt", "negativePrompt", "seed")) + "\n" + record["id"]
        kind = "upscale" if record.get("kind") == "upscale" else "generate"
        image = record.get("imagePath") if isinstance(record.get("imagePath"), str) else None
        available = int(record["status"] == "succeeded" and setup.artifact_image(root, record) is not None)
        db.execute("INSERT OR REPLACE INTO jobs VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                   (path.name, stat.st_mtime_ns, stat.st_size, record["status"], kind, search.casefold(), image, available, json.dumps(record)))
    db.execute("DELETE FROM jobs WHERE name NOT IN (SELECT name FROM seen)")
    db.commit()


def select(db, root, query, kind, offset, limit, cancel):
    # instr is literal substring matching: %, _, quotes and SQL-looking prompts need no special syntax.
    where = "status = 'succeeded' AND available = 1 AND instr(search, ?) > 0"
    parameters = [query]
    if kind != "all":
        where += " AND kind = ?"
        parameters.append(kind)
    total = db.execute("SELECT count(*) FROM jobs WHERE " + where, parameters).fetchone()[0]
    # Clamp a page made empty by an external deletion back to the last available page.
    offset = min(offset, ((total - 1) // limit) * limit) if total else 0
    results = []
    for row in db.execute("SELECT record FROM jobs WHERE " + where + " ORDER BY mtime DESC, name DESC LIMIT ? OFFSET ?",
                          parameters + [limit, offset]):
        setup.check_cancel(cancel)
        record = json.loads(row[0])
        image = setup.artifact_image(root, record)
        if image is not None:
            results.append(dict(record, imagePath=str(image), settings=setup.resolve_settings(root, record)))
    problems = []
    # Keep diagnostics independent of image filters, so a search cannot hide an interrupted job.
    for row in db.execute("SELECT record FROM jobs WHERE status IN ('failed', 'cancelled', 'interrupted') ORDER BY mtime DESC, name DESC LIMIT ?",
                          (setup.LIBRARY_PROBLEMS,)):
        setup.check_cancel(cancel)
        record = json.loads(row[0])
        log = setup.job_log(root, record)
        problems.append({"id": record["id"], "status": record["status"], "message": str(record.get("message", ""))[:2000],
                         "startedUtc": record.get("startedUtc", ""), "settings": setup.resolve_settings(root, record) or None,
                         "logPath": str(log) if log else None})
    return {"artifacts": results, "problems": problems, "total": total, "offset": offset,
            "hasMore": offset + limit < total}


def listing(payload, cancel=None):
    query, kind, offset, limit = options(payload)  # Validate before creating any folders.
    cancel = cancel or threading.Event()
    setup.check_cancel(cancel)
    root = setup.data_root(payload)
    directory = setup.owned(root, "library")
    directory.mkdir(exist_ok=True)
    # Independent of the inference lock. Serializes index writers and any rebuild after corruption.
    with setup.exclusive(directory):
        path = setup.owned(directory, "index.sqlite3")
        for suffix in ("-journal", "-wal", "-shm"):
            setup.owned(directory, "index.sqlite3" + suffix)
        for attempt in range(2):
            db = sqlite3.connect(path, timeout=5)
            try:
                version = db.execute("PRAGMA user_version").fetchone()[0]
                if version not in (0, VERSION):
                    db.close()
                    path.unlink()
                    continue  # All versions of this file are disposable; JSON never changes.
                initialize(db)
                synchronize(db, root, cancel)
                running = [setup.owned(root, "jobs/" + row[0]) for row in db.execute("SELECT name FROM jobs WHERE status = 'running'")]
                recovered = setup.recover_if_idle(root, running)
                if recovered:
                    synchronize(db, root, cancel)
                result = select(db, root, query, kind, offset, limit, cancel)
                return dict(result, recovered=recovered)
            except sqlite3.DatabaseError as exc:
                if attempt or (getattr(exc, "sqlite_errorcode", 0) & 255) not in (sqlite3.SQLITE_CORRUPT, sqlite3.SQLITE_NOTADB):
                    raise
                db.close()
                path.unlink(missing_ok=True)
                setup.owned(directory, "index.sqlite3-journal").unlink(missing_ok=True)
            finally:
                db.close()
    raise setup.SetupError("The Library index could not be rebuilt. Refresh Library to try again.")
