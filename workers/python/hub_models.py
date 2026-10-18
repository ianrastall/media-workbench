"""Pinned public Hub checkpoints, through the same native/file/GPU gates as local imports.

Only bounded safetensors headers are read before a user starts the large download. Model code,
remote configuration, pickle files and authentication are never used. Downloads go straight to
the owned import folder; there is no second multi-gigabyte copy or persistent download cache.
"""
from datetime import datetime, timezone
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import hub_catalog as catalog
import inference_setup as setup
import model_library as models
import storage

HEADER_LIMIT = 8 * 1024**2
NOTICE_LIMIT = 1024**2
NOTICE_NAMES = {"readme.md", "license", "license.md", "license.txt", "notice", "notice.md", "notice.txt"}


class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlsplit(newurl)
        host = parsed.hostname or ""
        if parsed.scheme != "https" or parsed.port not in (None, 443) or parsed.username or parsed.password \
                or not (host == "huggingface.co" or host.endswith((".huggingface.co", ".hf.co"))):
            raise setup.SetupError("The Hub download redirected to an unsupported server. No model was added.")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def open_file(repo, revision, path, start=None, end=None):
    # All three values have passed the native plan. Never accept a client-supplied URL.
    url = catalog.BASE + "/" + urllib.parse.quote(repo, safe="/") + "/resolve/" + revision + "/" + urllib.parse.quote(path, safe="/")
    headers = {"User-Agent": "MediaWorkbench (public-model-download)", "Accept-Encoding": "identity"}
    if start is not None:
        headers["Range"] = f"bytes={start}-{end}"
    try:
        return urllib.request.build_opener(HttpsRedirect()).open(urllib.request.Request(url, headers=headers), timeout=30)
    except urllib.error.HTTPError as exc:
        raise setup.SetupError(f"The Hub download returned HTTP {exc.code}. Check public access and try again.") from None
    except (OSError, urllib.error.URLError) as exc:
        raise setup.SetupError("The Hub download could not be reached. Check your connection and try again.") from None


def read_exact(response, count, cancel):
    chunks = []
    while count:
        setup.check_cancel(cancel)
        chunk = response.read(min(count, 256 * 1024))
        if not chunk:
            raise setup.SetupError("The Hub returned an incomplete model file. Nothing was added.")
        chunks.append(chunk)
        count -= len(chunk)
    return b"".join(chunks)


def read_range(repo, revision, file, start, end, cancel):
    with open_file(repo, revision, file["path"], start, end) as response:
        if response.status == 206:
            expected = f"bytes {start}-{end}/{file['bytes']}"
            if response.headers.get("Content-Range") != expected:
                raise setup.SetupError("The Hub returned an unexpected file range. Refresh the model details.")
        elif response.status == 200 and start == 0:
            pass  # Some storage servers ignore Range. Read only the bounded header and close.
        elif response.status == 200 and start == 8:
            read_exact(response, 8, cancel)
        else:
            raise setup.SetupError("The Hub could not provide this model's file header.")
        return read_exact(response, end - start + 1, cancel)


def identify_remote(repo, revision, file, cancel):
    prefix = read_range(repo, revision, file, 0, 7, cancel)
    length, = struct.unpack("<Q", prefix)
    if not 2 <= length <= min(HEADER_LIMIT, file["bytes"] - 8):
        raise models.Refused("This file has an unreadable or unusually large model header. Choose another file.")
    raw = read_range(repo, revision, file, 8, length + 7, cancel)
    return models.identify_checkpoint(models.parse_header(raw, file["bytes"]))


def native_plan(app, data, details):
    siblings = data.get("siblings") or []
    if not isinstance(siblings, list) or len(siblings) > 512:
        raise catalog.CatalogError("This repository has too many files for a safe download plan. Choose a single-file model repository.")
    files = []
    for entry in siblings:
        if not isinstance(entry, dict):
            continue
        lfs = entry.get("lfs") if isinstance(entry.get("lfs"), dict) else {}
        size = entry.get("size")
        if isinstance(entry.get("rfilename"), str) and type(size) is int and size >= 0:
            files.append(dict(path=entry["rfilename"], bytes=size, sha256=str(lfs.get("sha256") or "")))
    request = json.dumps(dict(repoId=details["id"], revision=details["revision"], license=details["license"],
                              gated=details["access"] != "Public", files=files)).encode("utf-8")
    function = storage.library(Path(app), "mw_plan_hub_model").mw_plan_hub_model
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    required = ctypes.c_size_t()
    code = function(request, len(request), None, 0, ctypes.byref(required))
    if code != 2 or not 0 < required.value <= 65536:
        raise setup.SetupError(f"The native Hub planner could not size its response ({code}).")
    output = ctypes.create_string_buffer(required.value)
    code = function(request, len(request), output, len(output), ctypes.byref(required))
    if code != 0 or required.value > len(output):
        raise setup.SetupError(f"The model metadata cannot form a safe download plan ({code}).")
    return json.loads(output.raw[:required.value])


def plan(app, payload, cancel, fetch=None):
    root = Path(payload.get("dataRoot") or "")
    if not root.is_absolute():
        raise ValueError("An absolute dataRoot is required")
    setup.check_cancel(cancel)
    repo, revision = payload.get("repoId", ""), payload.get("revision", "")
    data = catalog.metadata(repo, revision, fetch)
    details = catalog.describe(data, repo)
    planned = native_plan(app, data, details)
    files = planned["files"]
    selected = payload.get("file", "")
    if not isinstance(selected, str):
        raise ValueError("Choose a model file")
    file = next((item for item in files if item["path"] == selected), None) if selected else next(iter(files), None)
    if selected and file is None:
        raise ValueError("That file is not offered by this pinned model revision. Refresh its details.")
    result = dict(details=details, files=files, selectedFile=file, canInstall=False, message=planned["blocker"],
                  warnings=[], ownVae=False, headerChecked=False)
    if result["message"]:
        return result
    try:
        found, choice = setup.check_hardware(app, root)
    except setup.SetupError as exc:
        result["message"] = str(exc)
        return result
    result["warnings"] = choice["warnings"]
    # The native drive query creates nothing, including for a new data folder.
    if file["bytes"] + models.IMPORT_HEADROOM_BYTES > found["drive"]["freeBytes"]:
        result["message"] = f"Downloading this model needs {(file['bytes'] + models.IMPORT_HEADROOM_BYTES) / 1024**3:.1f} GiB free in the data folder. Free space or choose another drive in Settings."
        return result
    try:
        identified = identify_remote(details["id"], details["revision"], file, cancel)
        result.update(ownVae=identified["ownVae"], architecture=identified["family"], headerChecked=True)
    except models.Refused as exc:
        result["message"] = str(exc)
        return result
    family = models.FAMILY_NAMES[identified["family"]]
    if not models.bundled_ready(app, root):
        result["message"] = f"This file matches {family}. Set up Stable Diffusion 1.5 in Create first; downloaded models share its verified runtime. Then check this file again."
        return result
    if identified["family"] == "sdxl" and not models.xl_ready(app, root):
        result["message"] = f"This file matches {family}. {models.XL_NEEDED} Then check this file again."
        return result
    result.update(canInstall=True, message=f"This file matches {family} and fits this computer's supported profile. Download and test makes a "
                  f"{'1024 × 1024' if identified['family'] == 'sdxl' else 'small'} picture on your GPU; only a passing model becomes available in Create.")
    return result


def download(app, root, repo, revision, entry, destination, cancel, progress, git_blob=False):
    """Bounded streaming into an owned partial file; no finished file exists on failure."""
    partial = destination.with_suffix(destination.suffix + ".partial")
    digest = hashlib.sha1(usedforsecurity=False) if git_blob else hashlib.sha256()
    if git_blob:
        digest.update(f"blob {entry['bytes']}\0".encode())
    completed, last_space_check, last_progress = 0, 0, 0
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        with open_file(repo, revision, entry["path"]) as response, partial.open("xb") as stream:
            if response.status != 200:
                raise setup.SetupError("The Hub did not return the complete pinned model file.")
            length = response.headers.get("Content-Length")
            if length is not None and length != str(entry["bytes"]):
                raise setup.SetupError("The model download size changed. Nothing was added.")
            while True:
                setup.check_cancel(cancel)
                # read1 returns available socket data instead of waiting to fill a megabyte;
                # progress/cancellation remain responsive on a slow or throttled connection.
                read = getattr(response, "read1", response.read)
                chunk = read(min(1024**2, entry["bytes"] - completed + 1))
                if not chunk:
                    break
                completed += len(chunk)
                if completed > entry["bytes"]:
                    raise setup.SetupError("The Hub sent more data than the pinned file declares. Nothing was added.")
                stream.write(chunk)
                digest.update(chunk)
                if time.monotonic() - last_progress >= 0.25 or completed == entry["bytes"]:
                    progress(dict(stage="Downloading " + entry["path"], completed=completed, total=entry["bytes"], unit="bytes"))
                    last_progress = time.monotonic()
                if completed - last_space_check >= 32 * 1024**2:
                    free = storage.drive_info(app, root)["freeBytes"]
                    if free < entry["bytes"] - completed + setup.WRITE_HEADROOM_BYTES:
                        raise setup.SetupError("The data drive is running out of space. Free space and try again; the unfinished model is removed.")
                    last_space_check = completed
            if completed != entry["bytes"] or digest.hexdigest() != entry["digest"]:
                raise setup.SetupError("The model download did not match its pinned size and checksum. Nothing was added.")
            stream.flush()
            os.fsync(stream.fileno())
        setup.check_cancel(cancel)
        os.replace(partial, destination)
    finally:
        partial.unlink(missing_ok=True)


def install(app, payload, cancel, progress):
    if payload.get("licenseReviewed") is not True:
        raise ValueError("Review the publisher's model card and license before downloading.")
    # Resolve only the immutable revision the user reviewed. Never silently switch to current main.
    if not re.fullmatch(r"[0-9a-f]{40}", str(payload.get("revision", ""))) or not payload.get("file"):
        raise ValueError("Load and check the model details before downloading.")
    checked = plan(app, payload, cancel)
    if not checked["canInstall"]:
        raise models.Refused(checked["message"])
    file, details = checked["selectedFile"], checked["details"]
    if payload.get("sha256") != file["sha256"] or type(payload.get("bytes")) is not int or payload["bytes"] != file["bytes"]:
        raise ValueError("The selected file differs from the plan you reviewed. Refresh its details.")
    root = setup.data_root(payload)
    with setup.exclusive(root):
        setup.recover_interrupted(root)
        if not models.bundled_ready(app, root):
            raise setup.SetupError("Install / verify Stable Diffusion 1.5 in Create first.")
        if storage.drive_info(app, root)["freeBytes"] < file["bytes"] + models.IMPORT_HEADROOM_BYTES:
            raise setup.SetupError("The data folder no longer has enough free space for this download.")
        for other in models.imported_records(root):
            if any(item.get("sha256") == file["sha256"] for item in other["files"]):
                raise models.Refused(f"This model is already added as “{other['name']}”.")
        model_id = uuid.uuid4().hex
        folder = models.folder_of(root, model_id)
        folder.mkdir(parents=True)
        source = dict(provider="huggingface", repoId=details["id"], revision=details["revision"],
                      file=file["path"], sha256=file["sha256"], license=details["license"])
        record = dict(schemaVersion=1, id=model_id, name=models.unique_name(root, models.clean_name(details["id"].split("/")[-1])),
                      format="single-file", architecture=checked.get("architecture", "sd15"), ownVae=checked["ownVae"], status="copying",
                      message="Downloading the pinned model.", startedUtc=datetime.now(timezone.utc).isoformat(),
                      sourceName=details["id"] + "/" + file["path"], source=source, files=[], notices=[])
        try:
            setup.atomic_json(models.record_path(root, model_id), record)
            download(app, root, details["id"], details["revision"], dict(file, digest=file["sha256"]),
                     setup.owned(folder, "model.safetensors"), cancel, progress)
            record["files"] = [dict(path="model.safetensors", size=file["bytes"], sha256=file["sha256"])]
            # Retain pinned upstream model-card/license/notice files; never load their code/config.
            data = catalog.metadata(details["id"], details["revision"])
            seen_notices = set()
            for notice in data.get("siblings") or []:
                name = notice.get("rfilename", "") if isinstance(notice, dict) else ""
                if name.lower() not in NOTICE_NAMES:
                    continue
                if name.lower() in seen_notices:
                    raise models.Refused("The publisher's notice filenames conflict on Windows. Nothing was added.")
                seen_notices.add(name.lower())
                size, blob = notice.get("size"), notice.get("blobId", "")
                if type(size) is not int or not 0 <= size <= NOTICE_LIMIT or not re.fullmatch(r"[0-9a-f]{40}", str(blob)):
                    raise models.Refused("The publisher's model card or license could not be preserved safely. Nothing was added.")
                target = setup.owned(folder, "notices/" + name)
                download(app, root, details["id"], details["revision"], dict(path=name, bytes=size, digest=blob), target, cancel, progress, git_blob=True)
                record["notices"].append(dict(path="notices/" + name, size=size, sha256=setup.digest(target)))
            return models.finish_import(app, root, record, cancel, progress)
        except BaseException:
            shutil.rmtree(folder, ignore_errors=True)
            raise
