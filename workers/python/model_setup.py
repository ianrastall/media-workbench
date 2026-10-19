"""Read-only, combined preflight for the curated Models page; no AI imports or readiness writes."""
import shutil

import addon
import hardware
import inference_setup as setup
import sdxl


def plans(app, payload):
    root = setup.data_root(payload)
    found = hardware.detect(root, app)
    minimum = setup.minimum_vram_mib(app)
    base_ready = addon.base_ready(app, root)
    free = shutil.disk_usage(root).free
    base_needed = 0 if base_ready else setup.required_free_bytes(app, root)
    # File-size presence is only a download estimate. Setup still hashes files and tests inference.
    base_download = 0 if base_ready else sum(entry["size"] for _, path, entry in setup.model_files(app, root)
        if not path.is_file() or path.stat().st_size != entry["size"])
    xl_ready = sdxl.entry(app, root)["status"] == "ready"
    xl_download = 0 if xl_ready else sdxl.XL.missing_bytes(app, root)
    result = []
    for model, ready, download, needed, choice in (
        (setup.RECIPE, base_ready, base_download, base_needed, hardware.choose(found, minimum)),
        (sdxl.MODEL, xl_ready, base_download + xl_download,
         base_needed + (0 if xl_ready else xl_download + setup.SETUP_HEADROOM_BYTES), hardware.choose_xl(found, minimum)),
    ):
        blocker = choice["blocked"] or ""
        if not blocker and free < needed:
            blocker = (f"This setup needs about {setup.size_text(needed)} free in your working folder; "
                       f"{setup.size_text(free)} is free. Free up space or move the working folder in Settings.")
        result.append(dict(model=model, ready=ready and not blocker, requiresBase=not base_ready, downloadBytes=download,
                           requiredBytes=needed, freeBytes=free, blocker=blocker,
                           notes=list(choice["warnings"]) + list(choice["reasons"])))
    return {"plans": result}
