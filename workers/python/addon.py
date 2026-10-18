"""Optional verified setups beside Stable Diffusion 1.5: short videos and Stable Diffusion XL.

Standard library only. Each add-on has pinned files, its own runtime choices taken from the hardware,
a real GPU test that falls back to a conservative choice, and a readiness record written last. It
shares the Stable Diffusion 1.5 runtime, so it is ready only while that installation is ready too.
"""
from datetime import datetime, timezone
import hashlib
import json
import shutil
import threading
import uuid

import hardware
import inference_setup as setup


class Addon:
    def __init__(self, key, name, folder, manifest, runtimes, conservative, adapter_version, stamped_files, setup_action,
                 files_label, plural=False):
        """`name` reads mid-sentence ("short videos", "Stable Diffusion XL"); `files_label` names what is downloaded."""
        self.key, self.name, self.folder, self.manifest_name = key, name, folder, manifest
        self.runtimes, self.conservative, self.adapter_version = runtimes, conservative, adapter_version
        self.stamped_files, self.setup_action, self.files_label = stamped_files, setup_action, files_label
        self.title = name[:1].upper() + name[1:]
        self.are = "are" if plural else "is"
        self.not_ready = f"{setup_action} first: it downloads {files_label} and tests it on your GPU."

    def state_path(self, root):
        return setup.owned(root, f"jobs/install-{self.key}.json")

    def manifest(self, app):
        return json.loads((app / "profiles" / self.manifest_name).read_text(encoding="utf-8"))

    def model_files(self, app, root):
        """(stamp key, path, manifest entry) for each pinned file."""
        folder = setup.owned(root, f"models/{self.folder}")
        files = []
        for entry in self.manifest(app)["files"]:
            relative = entry.get("path") or entry["name"]
            files.append((f"{self.folder}/{relative}", setup.owned(folder, relative), entry))
        return files

    def profile_stamp(self, app, runtime):
        settings = self.runtimes[runtime]
        lines = [f"{self.key}-adapter={self.adapter_version}", f"runtime={runtime}:{settings['precision']}:offload={settings['offload']}"]
        for name in self.stamped_files:
            content = (app / name).read_bytes().replace(b"\r\n", b"\n")
            lines.append(f"{name}={hashlib.sha256(content).hexdigest()}")
        return hashlib.sha256("\n".join(lines).encode()).hexdigest()

    def stamp_valid(self, app, record):
        runtime = record.get("runtime")
        return runtime in self.runtimes and record.get("profileStamp") == self.profile_stamp(app, runtime)

    def files_unchanged(self, app, root, record):
        stamps = record.get("files")
        if not isinstance(stamps, dict):
            return False
        for key, path, _ in self.model_files(app, root):
            try:
                if setup.file_stamp(path) != stamps.get(key):
                    return False
            except OSError:
                return False
        return True

    def checked(self, app, root, record):
        """Read-only readiness projection; also needs a ready Stable Diffusion 1.5 installation."""
        record = dict(record)
        if record.get("status") != "ready":
            return record
        if not self.stamp_valid(app, record):
            record.update(status="needs_verification", message=f"The settings for {self.name} changed in this version. Select {self.setup_action} to test them again.")
        elif not self.files_unchanged(app, root, record):
            record.update(status="needs_verification", message=f"The files for {self.name} are missing or changed. Select {self.setup_action} again.")
        elif not base_ready(app, root):
            record.update(status="needs_verification", message=f"Stable Diffusion 1.5 needs Install / verify before {self.name} can be used.")
        return record

    def public(self, record):
        record = {key: value for key, value in record.items() if key != "files"}
        runtime = record.get("runtime")
        if runtime in self.runtimes:
            record["settings"] = dict(id=runtime, **{key: self.runtimes[runtime][key] for key in ("label", "description", "precision")})
        return record

    def status(self, app, payload, missing):
        root = setup.data_root(payload)
        setup.recover_if_idle(root)
        path = self.state_path(root)
        if not path.is_file():
            return {"status": "not_installed", "message": missing}
        record = setup.read_record(path)
        if record is None:
            return {"status": "needs_verification", "message": f"The setup record for {self.name} is damaged. Select {self.setup_action} again."}
        return self.public(self.checked(app, root, record))

    def ready_record(self, app, root):
        path = self.state_path(root)
        record = setup.read_record(path) if path.is_file() else None
        if not record:
            raise setup.SetupError(self.not_ready)
        record = self.checked(app, root, record)
        if record.get("status") != "ready":
            raise setup.SetupError(record.get("message") or self.not_ready)
        return record

    def missing_bytes(self, app, root):
        return sum(entry["size"] for _, path, entry in self.model_files(app, root)
                   if not path.is_file() or path.stat().st_size != entry["size"])

    def declined_here(self, item, gpu, runtime):
        return (item.get("runtime") == runtime and item.get("gpu") == gpu.get("name")
                and item.get("driver") == gpu.get("driver") and item.get("adapter") == self.adapter_version)

    def test_runtime(self, gpu, chosen, declined, attempt, progress, final=()):
        """Runs `attempt(runtime)` for the chosen runtime, then the conservative one; returns (runtime, evidence, declined).

        Exceptions of the types in `final` are not runtime problems, so they never fall back.
        """
        first = [] if any(self.declined_here(item, gpu, chosen) for item in declined) else [chosen]
        candidates = first + ([self.conservative] if chosen != self.conservative else [])
        for runtime in candidates:
            label = self.runtimes[runtime]["label"]
            progress({"stage": f"Testing {self.name} on your GPU with {label.lower()} settings"})
            try:
                return runtime, dict(attempt(runtime), runtime=runtime), declined
            except (setup.Cancelled, *final):
                raise
            except setup.SetupError as exc:
                if runtime == candidates[-1]:
                    raise
                declined = [item for item in declined if not self.declined_here(item, gpu, runtime)] + [dict(
                    runtime=runtime, gpu=gpu.get("name"), driver=gpu.get("driver"), adapter=self.adapter_version,
                    message=str(exc)[:500], failedUtc=datetime.now(timezone.utc).isoformat())]
                progress({"stage": f"{label} settings did not pass; testing memory-saving settings"})
        raise setup.SetupError(f"No {self.name} runtime choice was tested.")

    def install(self, app, payload, cancel, progress, choose, extra_bytes, fetch, attempt, final=()):
        """Download, verify, and GPU-test; ready is written last, atomically.

        `choose(found)` returns hardware.choose-shaped advice; `fetch(entries)` downloads the pinned files;
        `attempt(runtime)` runs the real test and returns its evidence.
        """
        from file_verification import verify
        root = setup.data_root(payload)
        with setup.exclusive(root):
            setup.recover_interrupted(root)
            if not base_ready(app, root):
                raise setup.SetupError(f"Set up Stable Diffusion 1.5 first (Install / verify in Create); it provides the verified runtime for {self.name}.")
            previous = setup.read_record(self.state_path(root)) if self.state_path(root).is_file() else None
            declined = [item for item in (previous or {}).get("declinedRuntimes") or [] if isinstance(item, dict)]
            source = self.manifest(app)
            record = dict(schemaVersion=1, attemptId=uuid.uuid4().hex, status="preparing", adapter=self.adapter_version,
                          startedUtc=datetime.now(timezone.utc).isoformat(),
                          source={key: source[key] for key in ("repoId", "revision", "license") if key in source})

            def stage(name, message):
                record.update(status=name, message=message)
                setup.atomic_json(self.state_path(root), record)
                progress({"stage": message})
            try:
                stage("preparing", f"Checking free space and the graphics card for {self.name}")
                needed = self.missing_bytes(app, root) + extra_bytes + setup.SETUP_HEADROOM_BYTES
                free = shutil.disk_usage(root).free
                if free < needed:
                    raise setup.SetupError(f"Setting up {self.name} needs about {setup.size_text(needed)} free on the data folder's drive; "
                                           f"{setup.size_text(free)} is free. Free up space or move the data folder in Settings.")
                found = hardware.detect(root, app)
                choice = choose(found)
                if choice["blocked"]:
                    raise setup.SetupError(choice["blocked"])
                stage("downloading", f"Fetching {self.files_label}")
                fetch(root, self.model_files(app, root))
                stage("verifying", f"Verifying the checksums of {self.files_label}")
                stamps = {}
                for key, path, entry in self.model_files(app, root):
                    before = setup.file_stamp(path)
                    verify(app, root, path, entry, cancel, progress, key)
                    if setup.file_stamp(path) != before:
                        raise setup.SetupError(f"{key} changed while it was checked. Select {self.setup_action} again.")
                    stamps[key] = before
                stage("verifying", f"Testing {self.name} on your GPU")
                runtime, evidence, declined = self.test_runtime(found["gpu"], choice["runtime"], declined, attempt, progress, final)
                settings = self.runtimes[runtime]
                message = f"{self.title} {self.are} ready on {evidence['gpu']} with {settings['label'].lower()} settings."
                if runtime != choice["runtime"]:
                    message += " Faster settings did not pass their test on this computer."
                record.update(status="ready", message=message, runtime=runtime, declinedRuntimes=declined,
                              profileStamp=self.profile_stamp(app, runtime), verifiedUtc=datetime.now(timezone.utc).isoformat(),
                              evidence=evidence, files=stamps)
                setup.atomic_json(self.state_path(root), record)
                return self.public(record)
            except Exception as exc:
                record.update(status="cancelled" if isinstance(exc, setup.Cancelled) else "failed", message=str(exc))
                setup.atomic_json(self.state_path(root), record)
                raise

    def remove(self, app, payload, cancel=None):
        """Removes the files and readiness record; pictures and videos already made are kept."""
        cancel = cancel if cancel is not None else threading.Event()
        root = setup.data_root(payload)
        with setup.exclusive(root):
            setup.recover_interrupted(root)
            self.state_path(root).unlink(missing_ok=True)  # First, so a partial removal never reads as ready.
            setup.check_cancel(cancel)
            folder = setup.owned(root, f"models/{self.folder}")
            if folder.is_dir():
                shutil.rmtree(folder)


def base_ready(app, root):
    record = setup.read_record(setup.state_path(root)) if setup.state_path(root).is_file() else None
    return bool(record) and setup.checked_installation(app, root, record).get("status") == "ready"
