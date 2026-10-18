"""What this computer has, and which verified way of running a model suits it.

Standard library only: the discovery interpreter and the GUI's worker read the hardware without
loading an AI runtime, and the ML child imports the same rules so both sides agree. Every runtime
choice here is a separate verified profile: an installation records the one that passed its real
GPU test, and inference uses only that one.
"""
import ctypes
import os
from pathlib import Path
import re
import shutil
import subprocess

MIB = 1024**2
# Drivers and Windows report slightly less than the nominal size (a 6 GB card reports 6143.7 MiB to
# CUDA; 8 GB of system memory usually shows as 7.8-7.9 GiB, less when integrated graphics borrow some).
VRAM_MARGIN_MIB = 128
RAM_MARGIN_MIB = 1024
CONSERVATIVE = "fp32-offload"
# The order is the order of preference; the conservative choice is last and works on every supported GPU.
RUNTIMES = {
    "fp16-gpu": dict(precision="float16", offload=False, minimumVramMiB=8192, label="Fast",
                     description="half precision, the whole model stays on the graphics card"),
    "fp16-offload": dict(precision="float16", offload=True, minimumVramMiB=6144, label="Fast, memory-saving",
                         description="half precision, parts of the model wait in system memory until needed"),
    "fp32-gpu": dict(precision="float32", offload=False, minimumVramMiB=10240, label="Full precision",
                     description="full precision, the whole model stays on the graphics card"),
    CONSERVATIVE: dict(precision="float32", offload=True, minimumVramMiB=6144, label="Memory-saving",
                       description="full precision, parts of the model wait in system memory until needed"),
}
MINIMUM_RAM_MIB = 8192  # Below this, loading the model alone would push Windows into heavy paging.
RECOMMENDED_RAM_MIB = 16384  # Memory-saving settings keep the model in system memory between steps.
GPU_FIELDS = ["index", "uuid", "name", "memory.total", "memory.free", "compute_cap", "driver_version"]
# Real-ESRGAN tile edge by free graphics memory after loading the upscaler (float32, 16-pixel overlap).
UPSCALE_TILES = [(5120, 512), (3072, 384), (1536, 256)]
UPSCALE_MIN_TILE = 128


class HardwareError(Exception):
    """A plain reason the graphics card could not be read."""


def half_precision_ok(gpu):
    """Whether half precision is trusted on this GPU.

    Needs compute capability 7.0 or newer. Turing cards without tensor cores (GTX 16-series, the
    Quadro T-series and MX cards, all 7.5) produce blank or broken images in half precision with
    Stable Diffusion, so they keep full precision; RTX cards and the Tesla T4 have tensor cores.
    """
    capability = parse_capability(gpu.get("computeCapability"))
    if capability is None or capability < (7, 0):
        return False
    name = str(gpu.get("name", "")).upper()
    return capability != (7, 5) or "RTX" in name or re.search(r"\bT4\b", name) is not None


def parse_capability(value):
    match = re.fullmatch(r"(\d+)\.(\d+)", str(value or "").strip())
    return (int(match[1]), int(match[2])) if match else None


def fits(vram_mib, minimum_mib):
    return vram_mib + VRAM_MARGIN_MIB >= minimum_mib


def allows(runtime, gpu):
    """Whether a runtime choice may run on this GPU (used again by the ML child before loading)."""
    settings = RUNTIMES.get(runtime)
    if settings is None or not gpu:
        return False
    if settings["precision"] == "float16" and not half_precision_ok(gpu):
        return False
    return fits(float(gpu.get("vramMiB") or 0), settings["minimumVramMiB"])


def choose(hardware, minimum_vram_mib):
    """The runtime this computer should use, why, and anything that limits it, in plain words.

    `hardware` is the shape `detect` returns. The result has `runtime` (None when blocked),
    `blocked` (a reason or None), `reasons`, and `warnings`.
    """
    gpu = hardware.get("gpu")
    ram = hardware.get("ramMiB")
    reasons, warnings = [], []
    def blocked(message):
        return dict(runtime=None, blocked=message, reasons=reasons, warnings=warnings)
    if not gpu:
        return blocked(hardware.get("gpuProblem") or "No NVIDIA graphics card was found. Making pictures needs an NVIDIA "
                       "graphics card with at least 6 GB of memory and an up-to-date NVIDIA driver.")
    vram = float(gpu.get("vramMiB") or 0)
    if not fits(vram, minimum_vram_mib):
        return blocked(f"{gpu['name']} has {gib(vram)} of graphics memory; Stable Diffusion 1.5 needs a graphics card "
                       f"with at least {gib(minimum_vram_mib)}.")
    if type(ram) in (int, float) and ram + RAM_MARGIN_MIB < MINIMUM_RAM_MIB:
        return blocked(f"This computer has {gib(ram)} of memory; making pictures needs at least {gib(MINIMUM_RAM_MIB)}. "
                       "Adding memory would make it possible.")
    runtime = next(name for name in RUNTIMES if allows(name, gpu))
    settings = RUNTIMES[runtime]
    if settings["precision"] == "float32":
        capability = parse_capability(gpu.get("computeCapability"))
        if capability is None:
            reasons.append("The NVIDIA driver did not report what this graphics card supports, so full precision is used. "
                           "Updating the driver may allow faster settings.")
        elif capability < (7, 0):
            reasons.append(f"{gpu['name']} is too old for fast half precision, so full precision is used.")
        else:
            reasons.append(f"{gpu['name']} can make blank pictures at half precision, so full precision is used.")
    if settings["offload"]:
        reasons.append(f"With {gib(vram)} of graphics memory, parts of the model wait in system memory until they are needed.")
    else:
        reasons.append(f"{gib(vram)} of graphics memory holds the whole model, so nothing waits in system memory.")
    if settings["offload"] and type(ram) in (int, float) and ram + RAM_MARGIN_MIB < RECOMMENDED_RAM_MIB:
        warnings.append(f"This computer has {gib(ram)} of memory. Pictures may be slow because the model waits in "
                        f"system memory; close other programs while making pictures. {gib(RECOMMENDED_RAM_MIB)} or more is recommended.")
    return dict(runtime=runtime, blocked=None, reasons=reasons, warnings=warnings)


def upscale_tile(free_vram_mib):
    """Largest Real-ESRGAN tile that fits the graphics memory free right now; smaller tiles are slower, not worse."""
    return next((tile for needed, tile in UPSCALE_TILES if free_vram_mib >= needed), UPSCALE_MIN_TILE)


def gib(mib):
    value = mib / 1024
    return f"{value:.0f} GB" if value >= 10 or abs(value - round(value)) < 0.05 else f"{value:.1f} GB"


def parse_gpus(listing, fields):
    """Rows of an nvidia-smi CSV listing (`--format=csv,noheader,nounits`) as dictionaries keyed by `fields`."""
    rows = []
    for line in listing.splitlines():
        if line.strip():
            parts = [part.strip() for part in line.split(",")]
            if len(parts) != len(fields):
                raise HardwareError("nvidia-smi returned an unrecognized GPU listing: " + line.strip()[:200])
            rows.append(dict(zip(fields, parts)))
    if not rows:
        raise HardwareError("nvidia-smi did not list any NVIDIA GPU. Check that the NVIDIA driver is installed.")
    return rows


def pick_gpu(rows, visible=None):
    """The row for the device the ML child sees as cuda:0.

    The child runs with CUDA_DEVICE_ORDER=PCI_BUS_ID, matching nvidia-smi ordering, so its device 0 is
    the first CUDA_VISIBLE_DEVICES entry, or index 0 when that is unset.
    """
    wanted = (visible or "").split(",")[0].strip() if visible is not None else "0"
    if not wanted:
        raise HardwareError("CUDA_VISIBLE_DEVICES hides every GPU; clear it or select an NVIDIA GPU.")
    match = next((row for row in rows if row["index"] == wanted or row["uuid"].startswith(wanted)), None)
    if match is None:
        raise HardwareError(f"CUDA_VISIBLE_DEVICES selects GPU '{wanted[:80]}', which nvidia-smi did not list.")
    return match


def number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def gpu_from_row(row):
    total = number(row.get("memory.total"))
    if total is None:
        raise HardwareError(f"The NVIDIA driver did not report memory for GPU {row['index']} ({row['name']}): it returned "
                            f"'{row.get('memory.total', '')[:40]}'. Update the driver, or select another GPU with CUDA_VISIBLE_DEVICES.")
    capability = row.get("compute_cap", "")
    free = number(row.get("memory.free"))
    return dict(name=row["name"], vramMiB=round(total), freeVramMiB=round(free) if free is not None else None,
                computeCapability=capability if parse_capability(capability) else "", driver=row.get("driver_version", ""))


def query_gpu():
    """The selected NVIDIA GPU from nvidia-smi; drivers older than R510 lack compute_cap and are asked without it."""
    executable = shutil.which("nvidia-smi")
    if os.name != "nt" or not executable:
        raise HardwareError("No NVIDIA graphics card was found (nvidia-smi is missing). Making pictures needs an NVIDIA "
                            "graphics card with at least 6 GB of memory and the NVIDIA driver installed.")
    for fields in (GPU_FIELDS, [field for field in GPU_FIELDS if field != "compute_cap"]):
        try:
            listed = subprocess.run([executable, "--query-gpu=" + ",".join(fields), "--format=csv,noheader,nounits"],
                         capture_output=True, text=True, timeout=10, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise HardwareError("nvidia-smi could not be run; check the NVIDIA driver: " + str(exc)) from None
        if listed.returncode == 0:
            return gpu_from_row(pick_gpu(parse_gpus(listed.stdout, fields), os.environ.get("CUDA_VISIBLE_DEVICES")))
        if "compute_cap" not in (listed.stdout + listed.stderr):
            break
    raise HardwareError("nvidia-smi failed; check the NVIDIA driver. " + (listed.stderr or listed.stdout).strip()[:300])


def system_memory():
    """(total, available) system memory in MiB, or (None, None) when it cannot be read."""
    if os.name == "nt":
        class Status(ctypes.Structure):
            _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong)] + \
                       [(name, ctypes.c_ulonglong) for name in ("total", "available", "pageTotal", "pageAvailable",
                                                                  "virtualTotal", "virtualAvailable", "extended")]
        status = Status(length=ctypes.sizeof(Status))
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return round(status.total / MIB), round(status.available / MIB)
        return None, None
    try:
        pages, size = os.sysconf("SC_PHYS_PAGES"), os.sysconf("SC_PAGE_SIZE")
        available = os.sysconf("SC_AVPHYS_PAGES")
        return round(pages * size / MIB), round(available * size / MIB)
    except (ValueError, OSError, AttributeError):
        return None, None


def drive_info(path, app=None):
    """Free space and drive type from Rust, shared with Create and Settings; creates nothing."""
    # Lazy import: storage also uses the inference coordinator, which imports this module.
    import storage
    app = Path(app) if app is not None else Path(__file__).resolve().parents[2]
    result = storage.drive_info(app, path)
    return dict(path=str(path), freeBytes=result["freeBytes"], totalBytes=result["totalBytes"], kind=result["driveKind"])


def detect(data_root, app=None):
    """The hardware profile: the selected NVIDIA GPU, system memory, and the data folder's drive."""
    try:
        gpu, problem = query_gpu(), None
    except HardwareError as exc:
        gpu, problem = None, str(exc)
    total, available = system_memory()
    return dict(gpu=gpu, gpuProblem=problem, ramMiB=total, availableRamMiB=available, drive=drive_info(data_root, app))
