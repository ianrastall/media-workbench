"""Data-folder storage: what each part uses, and user-requested removal of reproducible parts.

Standard library only. Removal holds the data-folder lock, so it never races setup or generation.
"""
import os
import shutil

from inference_setup import (PROFILE, RECIPE, check_cancel, data_root, exclusive, owned,
                             recover_interrupted, state_path)

# id, label, folders under the data root, what removing it means (None: not removable from the app).
AREAS = [
    ("runtime", "Inference runtime", [f"runtimes/{PROFILE}"], None),
    ("model", "Stable Diffusion 1.5 model", [f"models/{RECIPE}"], None),
    ("caches", "Download caches", ["cache", "bootstrap"],
     "Packages and archives kept so a repair need not download them again. Install / verify downloads them again if needed."),
    ("images", "Images", ["artifacts"], None),
    ("logs", "Logs", ["logs"], "Inference logs. Library's Open log buttons stop working for the removed logs."),
    ("jobs", "Job records", ["jobs"], None),
]
INSTALLATION = [f"runtimes/{PROFILE}", f"models/{RECIPE}", "cache", "bootstrap"]


def tree_size(path):
    """Bytes and files under path, without following links or junctions."""
    total = files = 0
    stack = [path]
    while stack:
        try:
            with os.scandir(stack.pop()) as entries:
                for entry in entries:
                    if entry.is_symlink() or (hasattr(entry, "is_junction") and entry.is_junction()):
                        continue
                    if entry.is_dir(follow_symlinks=False):
                        stack.append(entry.path)
                    elif entry.is_file(follow_symlinks=False):
                        total += entry.stat(follow_symlinks=False).st_size
                        files += 1
        except FileNotFoundError:
            continue  # Removed while measuring.
    return total, files


def summary(payload):
    root = data_root(payload)
    items = []
    for area, label, folders, note in AREAS:
        size = files = 0
        for folder in folders:
            path = owned(root, folder)
            if path.is_dir():
                folder_size, folder_files = tree_size(path)
                size, files = size + folder_size, files + folder_files
        items.append({"id": area, "label": label, "bytes": size, "files": files,
                      "clearable": note is not None, "note": note or ""})
    usage = shutil.disk_usage(root)
    installed = sum(item["bytes"] for item in items if item["id"] in ("runtime", "model", "caches"))
    return {"freeBytes": usage.free, "totalBytes": usage.total, "installationBytes": installed, "items": items}


def remove(root, folders, cancel, progress):
    for folder in folders:
        check_cancel(cancel)
        path = owned(root, folder)  # Refuses a link or junction on the way to the target.
        if path.is_dir():
            progress({"stage": "Removing " + folder})
            shutil.rmtree(path)  # Does not follow junctions inside the tree.


def clear(payload, cancel, progress):
    area = payload.get("area")
    root = data_root(payload)
    if area == "installation":
        folders = INSTALLATION
    else:
        match = next((entry for entry in AREAS if entry[0] == area), None)
        if match is None or match[3] is None:
            raise ValueError("Choose download caches, logs, or the installation to remove")
        folders = match[2]
    with exclusive(root):
        recover_interrupted(root)
        if area == "installation":
            state_path(root).unlink(missing_ok=True)  # First, so a partial removal can never read as ready.
        remove(root, folders, cancel, progress)
    return summary(payload)
