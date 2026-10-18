"""Stable Diffusion XL: larger, more detailed pictures as an optional verified setup.

Standard library only. SDXL base 1.0 is pinned by revision, size, and SHA-256 (fp16 safetensors, about
6.9 GB), downloaded by the ML child from the Hub at that revision, verified through Rust, and must pass
its own real 1024 x 1024 GPU test. It shares the Stable Diffusion 1.5 runtime and safety checker.
"""
from addon import Addon
import hardware
import inference_setup as setup

MODEL = "sdxl-base"
NAME = "Stable Diffusion XL"
# SDXL's trained sizes closest to square, portrait, and landscape.
SIZES = [(1024, 1024), (832, 1216), (1216, 832)]
TEST = dict(prompt="A small red ceramic house beside a green tree, watercolor illustration, daylight",
            negativePrompt="", seed=20261018, steps=4, width=1024, height=1024, guidanceScale=5.0)
MISSING = f"{NAME} is not set up. Setup downloads 6.9 GB and tests a picture on your GPU."
# Bump the adapter version when what a verified SDXL setup means changes (pipeline loading, precision, or test).
XL = Addon("sdxl", NAME, MODEL, "sdxl-files.json", hardware.XL_RUNTIMES, hardware.XL_CONSERVATIVE, 1,
           ["profiles/sdxl-files.json", "profiles/sd15-cu126.lock"], "Set up Stable Diffusion XL", "Stable Diffusion XL")


def status(app, payload):
    return XL.status(app, payload, MISSING)


def child_payload(app, root):
    return dict(runtime=XL.ready_record(app, root)["runtime"], model=MODEL)


def weight_bytes(app):
    return sum(entry["size"] for entry in XL.manifest(app)["files"] if entry["path"].endswith(".safetensors"))


def install(app, payload, cancel, progress):
    root = setup.data_root(payload)

    def fetch(_root, _files):
        # The ML child downloads Hub files at the pinned revision; Rust verifies each one afterwards.
        setup.inference(app, root, "download", {"manifest": XL.manifest_name}, cancel, progress)

    def attempt(runtime):
        return setup.inference(app, root, "verify-xl", dict(TEST, runtime=runtime, model=MODEL), cancel, progress)
    return XL.install(app, payload, cancel, progress, lambda found: hardware.choose_xl(found, setup.minimum_vram_mib(app)),
                      0, fetch, attempt)


def entry(app, root):
    """The models.list row: listed after Stable Diffusion 1.5, choosable once ready."""
    path = XL.state_path(root)
    record = setup.read_record(path) if path.is_file() else None
    state = XL.checked(app, root, record) if record else {"status": "not_installed", "message": MISSING}
    return dict(id=MODEL, name=NAME, status=state.get("status", "failed"), message=str(state.get("message", "")),
                format="bundled-xl", bytes=weight_bytes(app), builtIn=True, ownVae=True,
                verifiedUtc=str(state.get("verifiedUtc", "")), sourceName="")


def remove(app, payload, cancel=None):
    """Removes SDXL's files and readiness record; pictures already made are kept."""
    XL.remove(app, payload, cancel)
    return status(app, payload)
