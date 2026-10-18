"""Models the user already downloaded: identification, copying into the data folder, and readiness.

Standard library only; runs in the small discovery interpreter. Nothing here loads or executes
model code: a safetensors header is plain JSON, and pickle-based files (.ckpt, .bin, .pt) are
refused because opening them can run code. An added model is ready only after its copied files
pass native verification and the same real GPU inference check as the bundled model.

Added models are copied into `models/imported/<id>/` rather than used in place. The copy is the
file that was verified: moving, editing, or deleting the original cannot silently change or break
a ready model, native verification and readiness stamps only trust the application's own
link-free folder, and removing a model never touches the user's own files.
"""
from datetime import datetime, timezone
import ctypes
import json
from pathlib import Path
import re
import shutil
import struct
import time
import uuid

import inference_setup as setup

BUILT_IN = setup.RECIPE
BUILT_IN_NAME = "Stable Diffusion 1.5"
# Bump when loading or testing an added model changes what its ready state means.
# 2: the model runs with the bundled installation's runtime choice (or the conservative fallback) and records it.
IMPORT_ADAPTER_VERSION = 2
HEADER_MAX_BYTES = 100 * 1024**2
HEADER_MAX_TENSORS = 32000
JSON_MAX_BYTES = 1024**2
NAME_MAX = 80
IMPORT_HEADROOM_BYTES = 1024**3
FIRST_TEST = ("copying", "testing")  # An interrupted first import leaves nothing worth keeping.
PICKLE_SUFFIXES = {".ckpt", ".pt", ".pth", ".bin", ".pkl", ".pickle"}
ITEM_BYTES = {"F64": 8, "F32": 4, "F16": 2, "BF16": 2, "I64": 8, "I32": 4, "I16": 2, "I8": 1, "U64": 8,
              "U32": 4, "U16": 2, "U8": 1, "BOOL": 1, "F8_E4M3": 1, "F8_E5M2": 1}
WEIGHT_TYPES = {"F32", "F16", "BF16"}
INDEX_TYPES = {"I64", "I32"}  # Integer buffers such as CLIP's position_ids; quantized weights use other types.
NOT_SD15 = ("Media Workbench can add Stable Diffusion 1.5-family models for now, "
            "such as fine-tuned SD 1.5 checkpoints.")
# Weight files copied for each Diffusers part, preferring the half-size copy when both exist.
FOLDER_PARTS = {
    "unet": (["diffusion_pytorch_model.fp16.safetensors", "diffusion_pytorch_model.safetensors"], "unet/diffusion_pytorch_model.safetensors"),
    "text_encoder": (["model.fp16.safetensors", "model.safetensors"], "text_encoder/model.safetensors"),
    "vae": (["diffusion_pytorch_model.fp16.safetensors", "diffusion_pytorch_model.safetensors"], "vae/diffusion_pytorch_model.safetensors"),
}


class Refused(ValueError):
    """A plain-language reason this file or folder cannot be added."""


def read_header(path):
    """Tensor names to (dtype, shape) from a safetensors header, after checking it describes the whole file."""
    try:
        with Path(path).open("rb") as stream:
            size = Path(path).stat().st_size
            prefix = stream.read(8)
            if len(prefix) < 8:
                raise Refused("This file is too small to be a model.")
            (length,) = struct.unpack("<Q", prefix)
            if not 2 <= length <= min(HEADER_MAX_BYTES, size - 8):
                raise Refused("This is not a readable .safetensors model file.")
            raw = stream.read(length)
    except OSError as exc:
        raise Refused(f"The file could not be read: {exc.strerror or exc}") from None
    return parse_header(raw, size)


def parse_header(raw, size):
    """The same structural checks for local and bounded, pinned remote header bytes."""
    length = len(raw)
    if not 2 <= length <= min(HEADER_MAX_BYTES, size - 8):
        raise Refused("This is not a readable .safetensors model file.")
    try:
        header = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        raise Refused("This is not a readable .safetensors model file.") from None
    if not isinstance(header, dict):
        raise Refused("This is not a readable .safetensors model file.")
    header.pop("__metadata__", None)
    if len(header) > HEADER_MAX_TENSORS:
        raise Refused("This model header is too complicated to read safely. Choose another file.")
    payload = size - 8 - length
    tensors = {}
    end, ranges = 0, []
    for name, info in header.items():
        if not isinstance(info, dict) or info.get("dtype") not in ITEM_BYTES or not isinstance(info.get("shape"), list) \
                or len(info["shape"]) > 8 or not all(type(side) is int and 0 <= side <= 2**31 for side in info["shape"]) \
                or not isinstance(info.get("data_offsets"), list) or len(info["data_offsets"]) != 2 \
                or not all(type(offset) is int for offset in info["data_offsets"]):
            raise Refused("This .safetensors file has a damaged header.")
        start, stop = info["data_offsets"]
        count = ITEM_BYTES[info["dtype"]]
        for side in info["shape"]:
            count *= side
            if count > payload:
                raise Refused("This file is incomplete or has a damaged header.")
        if not 0 <= start <= stop or stop - start != count:
            raise Refused("This .safetensors file has a damaged header.")
        end = max(end, stop)
        if stop > start:
            ranges.append((start, stop))
        tensors[name] = (info["dtype"], tuple(info["shape"]))
    if end > payload:
        raise Refused("This file is incomplete. If it is still downloading, wait for it to finish and try again.")
    if end != payload or not tensors:
        raise Refused("This .safetensors file has a damaged header.")
    position = 0
    for start, stop in sorted(ranges):
        if start != position:
            raise Refused("This .safetensors file has overlapping or missing tensor data.")
        position = stop
    return tensors


def any_key(tensors, *fragments):
    return any(fragment in name for name in tensors for fragment in fragments)


def shape(tensors, name):
    entry = tensors.get(name)
    return entry[1] if entry else None


def refuse_other_kinds(tensors):
    """Recognizable files that are not complete SD 1.5 checkpoints, with what they are."""
    if any_key(tensors, "lora_up", "lora_down", "lora_A", "lora_B", "lora_unet_", "lora_te", ".lora."):
        raise Refused("This is a LoRA add-on that adjusts another model's style, not a complete model. "
                      "Adding LoRAs is not supported yet.")
    if any(name in tensors for name in ("emb_params", "clip_l", "clip_g")) or any_key(tensors, "string_to_param"):
        raise Refused("This is a textual-inversion embedding (a small style add-on), not a complete model.")
    if any_key(tensors, "conditioner.embedders.", "label_emb.0.0.weight", "add_embedding.linear_1"):
        raise Refused("This is a Stable Diffusion XL model. " + NOT_SD15)
    if any_key(tensors, "double_blocks.", "single_transformer_blocks."):
        raise Refused("This is a FLUX model. " + NOT_SD15)
    if any_key(tensors, "joint_blocks.", "transformer_blocks.0.attn.add_k_proj"):
        raise Refused("This is a Stable Diffusion 3 model. " + NOT_SD15)
    if any_key(tensors, "control_model.", "input_hint_block", "controlnet_cond_embedding"):
        raise Refused("This is a ControlNet add-on that guides another model, not a complete model.")
    if any_key(tensors, "cond_stage_model.model.token_embedding", "cond_stage_model.model.transformer."):
        raise Refused("This is a Stable Diffusion 2 model. " + NOT_SD15)


def check_unet(conv_in, cross, conv_out):
    if conv_in is None or conv_out is None or cross is None:
        raise Refused("This file is not a Stable Diffusion model that Media Workbench recognizes. " + NOT_SD15)
    if conv_in[:1] == (320,) and conv_in[2:] == (3, 3):
        if conv_in[1] == 9:
            raise Refused("This is a dedicated inpainting model. Painted changes already work with ordinary "
                          "Stable Diffusion 1.5 models; dedicated inpainting models are not supported yet.")
        if conv_in[1] == 8:
            raise Refused("This is an instruction-editing (InstructPix2Pix) model, which is not supported yet.")
    if cross == (320, 1024):
        raise Refused("This is a Stable Diffusion 2 model. " + NOT_SD15)
    if conv_in != (320, 4, 3, 3) or conv_out != (4, 320, 3, 3) or cross != (320, 768):
        raise Refused("This model's layout does not match Stable Diffusion 1.5. " + NOT_SD15)


def check_text_encoder(embedding):
    if embedding is None:
        raise Refused("This model has no text encoder, so it cannot read a description. Choose a complete model.")
    if embedding == (49408, 1024):
        raise Refused("This is a Stable Diffusion 2 model. " + NOT_SD15)
    if embedding != (49408, 768):
        raise Refused("This model's text encoder does not match Stable Diffusion 1.5. " + NOT_SD15)


def check_vae(decoder_in, encoder_out):
    if (decoder_in, encoder_out) != ((512, 4, 3, 3), (8, 512, 3, 3)):
        raise Refused("This model's picture decoder (VAE) does not match Stable Diffusion 1.5.")


def check_types(tensors, prefixes):
    if any(dtype not in WEIGHT_TYPES | INDEX_TYPES for name, (dtype, _) in tensors.items() if name.startswith(prefixes)):
        raise Refused("This model stores its weights in a compressed number format that is not supported. "
                      "Choose a 16- or 32-bit version of it.")


def identify_checkpoint(tensors):
    """A single-file (original layout) SD 1.5 checkpoint: returns whether it has its own VAE."""
    refuse_other_kinds(tensors)
    unet = "model.diffusion_model."
    if not any(name.startswith(unet) for name in tensors):
        if "conv_in.weight" in tensors and any(name.startswith("down_blocks.") for name in tensors):
            raise Refused("This file is only one part of a model (its UNet). Choose the model's whole folder, "
                          "or a single-file checkpoint.")
        if "encoder.conv_in.weight" in tensors and "decoder.conv_in.weight" in tensors:
            raise Refused("This is only a picture decoder (VAE), not a complete model.")
        if any(name.startswith("text_model.") for name in tensors):
            raise Refused("This is only a text encoder, not a complete model.")
        raise Refused("This file is not a Stable Diffusion model that Media Workbench recognizes. " + NOT_SD15)
    check_unet(shape(tensors, unet + "input_blocks.0.0.weight"),
               shape(tensors, unet + "input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight"),
               shape(tensors, unet + "out.2.weight"))
    check_text_encoder(shape(tensors, "cond_stage_model.transformer.text_model.embeddings.token_embedding.weight")
                       or shape(tensors, "cond_stage_model.transformer.embeddings.token_embedding.weight"))
    own_vae = any(name.startswith("first_stage_model.") for name in tensors)
    if own_vae:
        check_vae(shape(tensors, "first_stage_model.decoder.conv_in.weight"), shape(tensors, "first_stage_model.encoder.conv_out.weight"))
    check_types(tensors, (unet, "cond_stage_model.", "first_stage_model."))
    return own_vae


def read_json(path, what):
    try:
        if path.stat().st_size > JSON_MAX_BYTES:
            raise Refused(f"The model folder's {what} is too large to be a configuration file.")
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, ValueError):
        raise Refused(f"The model folder's {what} could not be read.") from None
    if not isinstance(value, dict):
        raise Refused(f"The model folder's {what} could not be read.")
    return value


def folder_part(folder, part, required):
    names, _ = FOLDER_PARTS[part]
    directory = folder / part
    for name in names:
        if (directory / name).is_file():
            return directory / name
    if directory.is_dir():
        if any(directory.glob("*.safetensors.index.json")):
            raise Refused(f"This model's {part} is split into several files, which is not supported yet.")
        if any(path.suffix.lower() in PICKLE_SUFFIXES for path in directory.iterdir()):
            raise Refused(f"This model's {part} is stored as a .bin (pickle) file, which can run hidden code when "
                          "opened. Media Workbench only accepts .safetensors files.")
    if required:
        raise Refused(f"This model folder has no {part.replace('_', ' ')} weights in .safetensors format.")
    return None


def identify_folder(folder):
    """A Diffusers-layout SD 1.5 folder: returns {part: source file} (no VAE means the bundled one is used)."""
    index = folder / "model_index.json"
    if not index.is_file():
        raise Refused("This folder has no model_index.json, so it is not a Diffusers model folder. "
                      "Choose a model folder, or a single .safetensors file.")
    pipeline = read_json(index, "model_index.json")
    kind = str(pipeline.get("_class_name", ""))
    if "XL" in kind or "text_encoder_2" in pipeline:
        raise Refused("This is a Stable Diffusion XL model. " + NOT_SD15)
    if kind not in {"StableDiffusionPipeline", "StableDiffusionImg2ImgPipeline", "StableDiffusionInpaintPipeline"}:
        raise Refused(f"This folder holds a {kind[:60] or 'different kind of'} model. " + NOT_SD15)
    parts = {part: folder_part(folder, part, part != "vae") for part in FOLDER_PARTS}
    unet = read_header(parts["unet"])
    refuse_other_kinds(unet)
    check_unet(shape(unet, "conv_in.weight"), shape(unet, "down_blocks.0.attentions.0.transformer_blocks.0.attn2.to_k.weight"),
               shape(unet, "conv_out.weight"))
    check_types(unet, ("",))
    text = read_header(parts["text_encoder"])
    check_text_encoder(shape(text, "text_model.embeddings.token_embedding.weight"))
    check_types(text, ("text_model.",))
    if parts["vae"] is not None:
        vae = read_header(parts["vae"])
        check_vae(shape(vae, "decoder.conv_in.weight"), shape(vae, "encoder.conv_out.weight"))
        check_types(vae, ("",))
    return {part: path for part, path in parts.items() if path is not None}


def suggested_name(path):
    """A readable default name; Hugging Face cache snapshots are named after their repository."""
    name = path.stem if path.is_file() else path.name
    if re.fullmatch(r"[0-9a-f]{40}", name) and path.parent.name == "snapshots" and path.parent.parent.name.startswith("models--"):
        name = path.parent.parent.name.split("--")[-1]
    return clean_name(name.replace("_", " ")) or "My model"


def clean_name(value):
    return " ".join("".join(c if c.isprintable() else " " for c in value).split())[:NAME_MAX]


def inspect(payload):
    """What would be added, without copying anything: the format, files, size, and a default name."""
    value = payload.get("path")
    if not isinstance(value, str) or not Path(value).is_absolute():
        raise ValueError("Choose the model file or folder by its full path")
    path = Path(value)
    if path.is_dir():
        files = {part: source.resolve() for part, source in identify_folder(path).items()}
        sources = [(FOLDER_PARTS[part][1], source) for part, source in files.items()]
        result = dict(format="diffusers", ownVae="vae" in files)
    elif path.is_file():
        suffix = path.suffix.lower()
        if suffix in PICKLE_SUFFIXES:
            raise Refused(f"{suffix} files can run hidden code when they are opened, so Media Workbench only adds "
                          ".safetensors files. Most model pages offer a .safetensors download of the same model.")
        if suffix != ".safetensors":
            raise Refused("Choose a .safetensors model file, or a Diffusers model folder.")
        result = dict(format="single-file", ownVae=identify_checkpoint(read_header(path)))
        sources = [("model.safetensors", path.resolve())]
    else:
        raise Refused("That file or folder could not be found.")
    size = sum(source.stat().st_size for _, source in sources)
    summary = (f"Stable Diffusion 1.5-family {'model folder' if result['format'] == 'diffusers' else 'model file'} · "
               f"{size / 1024**3:.2f} GiB. It will be copied into your data folder; the original stays where it is.")
    if not result["ownVae"]:
        summary += " It has no picture decoder (VAE) of its own, so Stable Diffusion 1.5's is used."
    return dict(result, suggestedName=suggested_name(path), bytes=size, summary=summary,
                files=[dict(path=target, source=str(source), size=source.stat().st_size) for target, source in sources])


def folder_of(root, model_id):
    if not setup.job_id_valid(model_id):
        raise ValueError("Choose one of the listed models")
    return setup.owned(root, f"models/imported/{model_id}")


def record_path(root, model_id):
    return setup.owned(folder_of(root, model_id), "model.json")


def read_model(root, model_id):
    path = record_path(root, model_id)
    record = setup.read_record(path) if path.is_file() else None
    if not record or record.get("id") != model_id or not isinstance(record.get("name"), str) \
            or record.get("format") not in ("single-file", "diffusers") or not isinstance(record.get("files"), list):
        return None
    return record


def stored_files(root, record):
    """(path, manifest entry) for the copied weights; app-written configs are watched by stamps too."""
    folder = folder_of(root, record["id"])
    return [(setup.owned(folder, entry["path"]), entry) for entry in record["files"] + record.get("notices", [])
            if isinstance(entry, dict) and isinstance(entry.get("path"), str)]


def watched_files(root, record):
    folder = folder_of(root, record["id"])
    return sorted(path for path in folder.rglob("*") if path.is_file() and path.name != "model.json"
                  and not path.name.endswith((".tmp", ".partial")))


def stamps(root, record):
    folder = folder_of(root, record["id"])
    return {path.relative_to(folder).as_posix(): setup.file_stamp(path) for path in watched_files(root, record)}


def bundled_ready(app, root):
    """The bundled installation an added model shares its runtime, tokenizer, and safety checker with."""
    state = setup.state_path(root)
    ready = setup.read_record(state) if state.exists() else None
    return bool(ready and setup.checked_installation(app, root, ready).get("status") == "ready")


def readiness(app, root, record, base_ready):
    """(status, message) of an added model. Ready requires its own passed test and a ready bundled installation."""
    if record.get("status") != "ready":
        return record.get("status", "failed"), str(record.get("message", ""))[:2000]
    if not base_ready:
        return "needs_verification", "Install / verify Stable Diffusion 1.5 in Create; added models use its runtime."
    if record.get("adapterVersion") != IMPORT_ADAPTER_VERSION or not setup.stamp_valid(app, record):
        return "needs_verification", "Media Workbench changed how models run. Test this model again."
    try:
        if stamps(root, record) != record.get("stamps"):
            return "needs_verification", "This model's copied files changed. Test it again."
    except OSError:
        return "needs_verification", "This model's copied files could not be read. Test it again."
    return "ready", "Ready"


def entry(app, root, record, base_ready):
    status, message = readiness(app, root, record, base_ready)
    size = sum(item.get("size", 0) for item in record["files"] if isinstance(item, dict) and type(item.get("size")) is int)
    return dict(id=record["id"], name=record["name"], status=status, message=message, format=record["format"],
                bytes=size, builtIn=False, ownVae=bool(record.get("ownVae", True)),
                verifiedUtc=str(record.get("verifiedUtc", "")), sourceName=str(record.get("sourceName", "")))


def imported_records(root):
    folder = setup.owned(root, "models/imported")
    if not folder.is_dir():
        return []
    records = [read_model(root, path.name) for path in folder.iterdir() if path.is_dir() and setup.job_id_valid(path.name)]
    return sorted((record for record in records if record), key=lambda record: (record["name"].casefold(), record["id"]))


def listing(app, payload):
    root = setup.data_root(payload)
    base = setup.status(app, payload)  # Also recovers interrupted work when the folder is idle.
    base_ready = base.get("status") == "ready" and bundled_ready(app, root)
    models = [dict(id=BUILT_IN, name=BUILT_IN_NAME, status=base.get("status", "not_installed"),
                   message=str(base.get("message", "")), format="bundled", bytes=0, builtIn=True, ownVae=True,
                   verifiedUtc=str(base.get("verifiedUtc", "")), sourceName="")]
    models += [entry(app, root, record, base_ready) for record in imported_records(root)]
    return {"models": models}


def ready_model(app, root, model_id):
    """The record of an added model that may generate now, or a plain reason it may not."""
    record = read_model(root, model_id)
    if record is None:
        raise setup.SetupError("That model is no longer in the data folder. Choose another model.")
    status, message = readiness(app, root, record, bundled_ready(app, root))
    if status != "ready":
        raise setup.SetupError(f"“{record['name']}” is not ready: {message} Test it again in Settings, or choose another model.")
    return record


def recover(root):
    """Remove first imports whose owner died; they never passed their test. Call while holding the lock."""
    folder = setup.owned(root, "models/imported")
    if not folder.is_dir():
        return 0
    removed = 0
    for path in folder.iterdir():
        if not path.is_dir() or not setup.job_id_valid(path.name):
            continue
        record = setup.read_record(path / "model.json") if (path / "model.json").is_file() else None
        if record is None or record.get("status") in FIRST_TEST:
            shutil.rmtree(setup.owned(root, f"models/imported/{path.name}"))
            removed += 1
    return removed


def native_copy(app, root, source, relative, cancel, progress, label):
    """Copy one file into the data folder through the Rust core, hashing it in the same pass."""
    from file_verification import load_core
    core = load_core(app, "mw_import_file")
    callback_type = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_void_p)
    failures = []
    last_report = 0.0

    @callback_type
    def on_progress(completed, total, _context):
        nonlocal last_report
        try:
            if cancel.is_set():
                return 1
            now = time.monotonic()
            if completed == 0 or completed == total or now - last_report >= 0.2:
                progress(dict(stage="Copying " + label, completed=completed, total=total, unit="bytes"))
                last_report = now
            return int(cancel.is_set())
        except BaseException as exc:
            failures.append(exc)
            return 1

    function = core.mw_import_file
    function.argtypes = [ctypes.c_char_p, ctypes.c_size_t, callback_type, ctypes.c_void_p,
                         ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    function.restype = ctypes.c_int32
    request = json.dumps(dict(source=str(source), dataRoot=str(root), relativePath=relative)).encode("utf-8")
    required = ctypes.c_size_t()
    output = ctypes.create_string_buffer(64)
    code = function(request, len(request), on_progress, None, output, len(output), ctypes.byref(required))
    if failures:
        raise failures[0]
    if code == 5:
        raise setup.Cancelled("Adding the model was cancelled. Nothing was added; your original file is unchanged.")
    if code == 6:
        raise setup.SetupError("The model file changed while it was being copied. Wait for anything still writing it to finish, then try again.")
    if code == 3:
        raise setup.SetupError("The model could not be copied. Check that it is not open in another program and that the data folder has free space.")
    if code != 0 or required.value != 64:
        raise setup.SetupError(f"The model could not be copied (native status {code}).")
    return output.raw.decode("ascii")


def unique_name(root, name, skip=None):
    taken = {record["name"].casefold() for record in imported_records(root) if record["id"] != skip}
    taken.add(BUILT_IN_NAME.casefold())
    candidate, number = name, 2
    while candidate.casefold() in taken:
        suffix = f" ({number})"
        candidate, number = name[:NAME_MAX - len(suffix)] + suffix, number + 1
    return candidate


def bundled_configs(root, folder, record):
    """Diffusers parts load with Stable Diffusion 1.5's own configuration; the user's configs are never used."""
    if record["format"] != "diffusers":
        return
    bundled = setup.owned(root, f"models/{BUILT_IN}")
    for part in ("unet", "text_encoder", "vae"):
        if (folder / part).is_dir():
            shutil.copyfile(bundled / part / "config.json", setup.owned(folder, f"{part}/config.json"))


def test_model(app, root, record, cancel, progress):
    """Native verification of the copied files, then the real GPU test. Returns the updated record."""
    from file_verification import verify
    folder = folder_of(root, record["id"])
    for path, item in stored_files(root, record):
        setup.check_cancel(cancel)
        verify(app, root, path, item, cancel, progress, item["path"])
    # The copy, not the original, is what runs: identify it again from its own headers.
    copied = identify_folder_copy(folder) if record["format"] == "diffusers" else identify_checkpoint(read_header(folder / "model.safetensors"))
    if record["format"] == "single-file" and copied != record["ownVae"]:
        raise Refused("The copied model does not match the file that was checked. Try adding it again.")
    before = stamps(root, record)
    progress({"stage": "Testing 512 × 512 pictures with this model on your GPU"})
    # The bundled installation's verified runtime first; a model that fails there may still pass the conservative one.
    bundled = setup.read_record(setup.state_path(root)) or {}
    runtime, evidence, _ = setup.verify_runtime(app, root, {}, bundled["runtime"], [], cancel, progress,
                                                "verify-model", child_payload(record))
    if stamps(root, record) != before:
        raise setup.SetupError("This model's copied files changed during its test. Test it again.")
    return dict(record, status="ready", message="Ready", verifiedUtc=datetime.now(timezone.utc).isoformat(), runtime=runtime,
                evidence=evidence, stamps=before, adapterVersion=IMPORT_ADAPTER_VERSION, profileStamp=setup.profile_stamp(app, runtime))


def identify_folder_copy(folder):
    """Copied Diffusers parts carry only weights and bundled configs; check their headers again."""
    unet = read_header(folder / FOLDER_PARTS["unet"][1])
    check_unet(shape(unet, "conv_in.weight"), shape(unet, "down_blocks.0.attentions.0.transformer_blocks.0.attn2.to_k.weight"),
               shape(unet, "conv_out.weight"))
    check_text_encoder(shape(read_header(folder / FOLDER_PARTS["text_encoder"][1]), "text_model.embeddings.token_embedding.weight"))
    vae = folder / FOLDER_PARTS["vae"][1]
    if vae.is_file():
        header = read_header(vae)
        check_vae(shape(header, "decoder.conv_in.weight"), shape(header, "encoder.conv_out.weight"))


def child_payload(record):
    payload = dict(model=record["id"], modelFormat=record["format"], ownVae=bool(record["ownVae"]))
    if record.get("runtime") in setup.hardware.RUNTIMES:
        payload["runtime"] = record["runtime"]  # A tested model runs as it was tested; a test supplies its own.
    return payload


def finish_import(app, root, record, cancel, progress):
    """One commit path for copied local models and pinned Hub downloads. Caller owns the folder lock."""
    folder = folder_of(root, record["id"])
    for other in imported_records(root):
        if other["id"] != record["id"] and sorted((f["path"], f["sha256"]) for f in other["files"]) == sorted((f["path"], f["sha256"]) for f in record["files"]):
            raise Refused(f"This model is already added as “{other['name']}”.")
    bundled_configs(root, folder, record)
    record.update(status="testing", message="Testing the model on your GPU.")
    setup.atomic_json(record_path(root, record["id"]), record)
    record = test_model(app, root, record, cancel, progress)
    setup.atomic_json(record_path(root, record["id"]), record)
    return entry(app, root, record, True)


def import_model(app, payload, cancel, progress):
    identified = inspect(payload)  # Before data_root, which creates the folder.
    name = payload.get("name", "")
    if not isinstance(name, str) or len(name) > 1000:
        raise ValueError("Give the model a name of at most 80 characters")
    name = clean_name(name) or identified["suggestedName"]
    root = setup.data_root(payload)
    with setup.exclusive(root):
        setup.recover_interrupted(root)
        if not bundled_ready(app, root):
            raise setup.SetupError("Install / verify Stable Diffusion 1.5 in Create first. Added models use its private runtime, so nothing else downloads.")
        needed = identified["bytes"] + IMPORT_HEADROOM_BYTES
        if shutil.disk_usage(root).free < needed:
            raise setup.SetupError(f"Adding this model needs {needed / 1024**3:.1f} GiB of free space in the data folder.")
        model_id = uuid.uuid4().hex
        folder = folder_of(root, model_id)
        folder.mkdir(parents=True)
        record = dict(schemaVersion=1, id=model_id, name=unique_name(root, name), format=identified["format"],
                      architecture="sd15", ownVae=identified["ownVae"], status="copying",
                      message="Copying the model into the data folder.", startedUtc=datetime.now(timezone.utc).isoformat(),
                      sourceName=Path(payload["path"]).name[:260], files=[])
        setup.atomic_json(record_path(root, model_id), record)
        try:
            for item in identified["files"]:
                setup.check_cancel(cancel)
                digest = native_copy(app, root, item["source"], f"models/imported/{model_id}/{item['path']}", cancel, progress, item["path"])
                record["files"].append(dict(path=item["path"], size=item["size"], sha256=digest))
            return finish_import(app, root, record, cancel, progress)
        except BaseException:
            # A model that never passed its test is not kept; the user's original is untouched.
            shutil.rmtree(folder, ignore_errors=True)
            raise


def retest(app, payload, cancel, progress):
    root = setup.data_root(payload)
    model_id = payload.get("modelId")
    folder_of(root, model_id)
    with setup.exclusive(root):
        setup.recover_interrupted(root)
        record = read_model(root, model_id)
        if record is None:
            raise ValueError("That model is no longer in the data folder.")
        if not bundled_ready(app, root):
            raise setup.SetupError("Install / verify Stable Diffusion 1.5 in Create first; added models use its runtime.")
        try:
            record = test_model(app, root, record, cancel, progress)
        except setup.Cancelled:
            raise  # The earlier result stands.
        except Exception as exc:
            record.update(status="failed", message=str(exc)[:2000])
            setup.atomic_json(record_path(root, model_id), record)
            raise
        setup.atomic_json(record_path(root, model_id), record)
        return entry(app, root, record, True)


def remove(app, payload):
    root = setup.data_root(payload)
    folder = folder_of(root, payload.get("modelId"))
    with setup.exclusive(root):
        setup.recover_interrupted(root)
        if not folder.is_dir():
            raise ValueError("That model is no longer in the data folder.")
        record_path(root, folder.name).unlink(missing_ok=True)  # First, so a partial removal never lists as ready.
        shutil.rmtree(folder)
    return listing(app, payload)
