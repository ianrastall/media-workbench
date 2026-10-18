# Development direction after 0.2.0-alpha.13

This is a plan, not a list of supported features. The implemented path is SD 1.5 text/image-to-image and painted partial changes, adding SD 1.5-family models the user already downloaded, Real-ESRGAN upscaling, durable job evidence, indexed Library browsing and PNG export, storage maintenance with automatic clean-up and free-space checks, runtime settings chosen from the computer's hardware, and checksum-verified app updates. Those operations share one saved data folder and preserve model/runtime verification gates.

## Purpose and next feature priority

Media Workbench aims to move image and eventually video generation onto the user's own computer, with offline use after setup and less reliance on online generation services. Video is not implemented. Energy, water, chip-demand, and cost benefits have not been measured; local generation still uses electricity and hardware. It stays free, MIT-licensed, and unlocked on the public repository. It must serve beginners as well as power users, so runtime, precision, and pipeline details stay under the hood behind plain choices.

The next user-facing priorities are:

1. **Add your own downloaded models.** Done for SD 1.5-family single files and Diffusers folders in 0.2.0-alpha.11; other architectures follow with their own verified profiles.
2. **Refresh the model list from Hugging Face.** Discover already searches live metadata. It should also say which results this computer can actually install and run (supported architecture, safetensors files, license shown, VRAM fit), and offer a pinned install with the same verification gate.
3. Then newer image models that fit 6 GB, and short video, as optional verified profiles.

Alongside these, every feature follows the product principles in `AGENTS.md`: easy to use, stable, aware of the computer it runs on, clean, and mindful of drive space. The section below says what that still requires.

## What to do next

The alpha.13 handoff is completed: hardware-aware inference is checked, drive detection is shared through Rust, and NuGet versions/locks, x64 enforcement, and native loading have been tightened. See [stack alignment](stack-alignment.md) and [handoff](../HANDOFF.md). The reference is an acceptance target; the older architecture proposals remain historical.

In order, as of 18 October 2026:

1. **Validate the faster runtime choices on real hardware.** `fp16-gpu`, `fp16-offload`, and `fp32-gpu` are chosen automatically on suitable cards but lack representative end-to-end hardware evidence. The completed GTX 1660 Ti check selects `fp32-offload`; draft notes about forced alternate settings on that card do not establish compatibility of the faster profiles. Run `tests/gpu_hardware_profile_check.py` on an RTX card with 8 GB or more (expects `fp16-gpu`), a 6 GB RTX card (`fp16-offload`), and if possible a Pascal card with 10 GB or more (`fp32-gpu`), record the results in `docs/validation.md`, and adjust the thresholds in `workers/python/hardware.py` if the measurements say so (precision/offloading changes require fresh verification; bump the adapter version for other inference changes).
2. **Close release and stack evidence gaps.** Run clean-machine validation below, check accessibility/high contrast/text scaling/mixed DPI, measure startup/idle behavior, preserve useful release symbols and build metadata, and verify a mixed managed/Rust breakpoint. Review the pinned Rust version against supported releases before another toolchain upgrade. These checks can proceed independently of access to more GPUs.
3. **Refresh the model list from Hugging Face** (priority 2 above), using the hardware profile to say which results this computer can run: VRAM fit, supported architecture, safetensors, license.
4. **Clean-machine validation** (below): installer, upgrade from alpha.12 (installations ask for Install / verify once because the readiness stamp changed), offline launch, update/relaunch.
5. **Notice a slow or busy drive at run time**: compare model-load time with what the drive kind predicts and say so when a job is slow (the static hard-disk advice already exists).
6. Then the warm inference worker and durable queue, newer models that fit 6 GB, and video, each as verified profiles.

## Standing principles: system awareness, clean-up, and drive space

`AGENTS.md` lists these as product principles for every feature. This is where the app stands against them and what to build.

**Already in place.** Setup checks the NVIDIA driver and the GPU's memory against the recipe's minimum, on the same device inference uses. Setup and adding a model check free space first. Settings → Storage shows free space and what each part of the data folder uses. Interrupted generations lose their partial image, unfinished record writes are removed, failed or interrupted model imports are deleted, painted-area hand-over files are deleted after use, and a failed update download is discarded. Download caches and logs can be cleared by hand, and automatic clean-up and free-space checks now follow the rules below.

**System awareness — done in 0.2.0-alpha.13, verified on one GPU.** A hardware profile (GPU name, compute capability, graphics memory, driver, system memory, data-drive free space and type) is read once per data folder per session without loading an AI runtime and shown in plain words in Diagnostics and on Create's setup card. It chooses one of four runtime choices (half or full precision, whole model on the GPU or offloaded), the upscaler tile size from free graphics memory, and refuses or warns plainly when memory is too small. Each runtime choice has its own readiness stamp and must pass the real GPU test; a failed faster choice falls back to the conservative `fp32-offload` and is not retried on the same card and driver. Drive information now uses Rust's `mw_drive_info` through the storage adapter; the duplicate Python implementation is removed. Diagnostics shares the ordinary installation-readiness checks and does not call missing or changed files verified. Still open: real-hardware validation of the three faster choices (item 1 under What to do next).

**Clean-up — done in 0.2.0-alpha.12.** Rust's `mw_tidy_data_folder` owns the rules and runs under the data-folder lock at startup and after a verified setup: unfinished-job records beyond the 50 Library lists (with their inputs and logs), logs beyond 300 or 30 days (never an unfinished job's), setup downloads left unfinished for 72 hours or after verification, and the download caches once setup is verified unless Settings keeps them. The desktop sweeps painted-area hand-overs and finished update installers from `%TEMP%`. Still open: pruning Library images themselves needs the organization and provenance export described under Later expansion first.

**Drive space and speed — done in 0.2.0-alpha.12, except load.** Generation and upscaling check free space before writing; Create, Tools, and Settings warn below 5 GiB; `mw_drive_info` detects a hard disk, USB, or network data folder and Create and Settings say that models load faster from an SSD and the folder can be moved. Still open: noticing that a drive is busy right now (for example, measuring model-load time against the drive kind) and saying so when a job is slow.

Acceptance: routine tests cover profile selection from fake hardware descriptions, each clean-up rule including what it must keep, crash leftovers, and low-space refusal before any write (all in place); opt-in GPU checks verify each runtime choice on real hardware (only `fp32-offload` so far).

## Immediate validation

Exercise the current installer and complete image workflow in clean Windows 10 22H2 and Windows 11 environments. Cover install, upgrade, uninstall/data preservation, offline launch after setup, and the app's update/relaunch sequence. Check the Library search, paging, settings reuse, save picker overwrite/cancel behavior, default image viewer, high DPI, narrow windows, and keyboard navigation. Record observed OS/GPU/driver evidence without implying certification of untested configurations. The current installer is unsigned; signing needs an actual certificate/distribution decision.

The hosted build does not provision models or run GPU inference. A green CI run is packaging and behavioral evidence, not a hardware compatibility result.

## Expand the native core

Artifact export, storage inventory, and pinned model-file verification are now Rust services. Inventory/export are called directly by the desktop/MCP/CLI; setup uses a thin Python adapter for model-file verification. Continue moving owned-storage pruning, artifact/job validation and indexing, durable queue state, and bootstrap/package verification into narrow, documented C ABI services. Keep path containment, atomic commit, cancellation, and file ownership inside Rust with meaningful boundary tests. C# should own presentation and host coordination; Python should focus on Hub integration, runtime provisioning, and the ML libraries that require it. Avoid adding a second implementation of domain policy in each client.

## LLM tool access

The local stdio MCP host is now implemented; see [LLM tools](llm-tools.md). Continue validating real clients and design authenticated remote access so a cloud-only LLM client can inspect the installation, plan/setup the supported model, and submit image/upscale jobs. The LLM client handles its own provider account; the application should not collect or embed provider account tokens. An account alone does not guarantee that a particular client supports local tools.

The implemented stdio host uses the existing C# service and Windows Job Object ownership. Require an explicit data folder, default to inspection tools, and grant setup downloads and GPU generation only through launch-time capabilities the model cannot enable for itself. Keep model IDs/runtime profiles constrained to the verified bundled recipes. Reuse the normal native plan and inference/readiness gates, bounded JSON messages, cancellation, and durable records. Tool outputs must describe actual installed/verified state and explicitly report that video is unsupported.

A cloud-only client needs an authenticated remote transport or a trusted local connector. Do not equate a local stdio server with an Internet-accessible endpoint. A later remote service needs scoped authorization, authenticated sessions, origin checks, TLS, auditing, and durable long-running job handles; it must not expose arbitrary shell execution, provider credentials, or unrestricted file access. See the official [MCP transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) and [tool protocol](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).

## Durable generation queue

Start with the existing job JSON as the authoritative store. Persist a queued job's complete settings and private input copy before acknowledging it. Distinguish queued, running, succeeded, failed, cancelled, and interrupted states. An explicit queue owner must claim work under the data-folder lock; separate clients must never run simultaneous GPU jobs in the same folder. Removing a pending item must not delete an already completed image. A failed job must not discard later queued items.

Pending work should be visible after a restart and require an explicit Resume action; do not start GPU work merely because Library was opened. Cancellation must distinguish cancelling the active job from removing pending work. Closing/crashing the app must still kill the owned process tree, and recovery must mark its active job interrupted without converting pending jobs to failed work.

The current global `IsBusy` and one-request-per-worker transport are deliberate limitations. Introduce separate inference/maintenance and browsing state before allowing enqueue or Library actions during generation. Folder changes, setup, installation removal, and updates must coordinate with the queue owner. Retain a clear UI representation of which saved folder owns pending jobs.

Acceptance checks: atomic enqueue and claim, input copies surviving source removal, restart without automatic inference, multiple-client exclusion, active versus pending cancellation, failure followed by another job, crash recovery, data-folder changes, and unchanged readiness gating. Keep routine tests free of model downloads and hardware requirements; use opt-in real GPU checks for the complete lifecycle.

## Then evaluate a warm inference worker

Measure model-load/import time separately from inference on the existing verified profile before choosing an idle policy. Keep the ML runtime in a child process owned by the same Windows Job Object; the GUI must never load PyTorch or Diffusers. Reuse a pipeline only for the same verified profile, model stamps, GPU selection, precision, and operation-compatible configuration. Sampler and image-to-image changes must not leak state into the next job.

Cancellation or protocol failure may discard the warm child and recreate it for the next queued job. Release it before install/verify, removal, saved-folder changes, updates, or shutdown, and after an explicit idle timeout. Account for retained CPU/GPU memory on 6 GB devices. A crash must still produce an interrupted active job and no partially committed Library image. Changes to inference loading or verification semantics require an adapter-version bump and fresh real inference checks.

## Later expansion

Add Library organization and portable settings/provenance export before destructive artifact pruning. Any deletion needs precise image/job/source relationships so a reused private input is not removed accidentally. Extend model adapters (including a dedicated inpainting checkpoint if quality requires it), protected Hub access, other hardware, and video only with explicit runtime/license/hardware validation. Discovery results must continue to describe metadata without implying installation support.
