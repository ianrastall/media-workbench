# Changelog

## Unreleased

- Upscaling: **Tools** makes a picture 2× or 4× bigger and sharper, offline, with Real-ESRGAN x4plus (pictures up to 2048 pixels on the longest side; a 768 × 512 picture becomes 3072 × 2048 in about 4 seconds on a GTX 1660 Ti). Choose or drag a picture, or select **Make it bigger** on a Library image or the latest result; the result is saved to Library.
- Install / verify now also downloads the 64 MiB upscaler weights (pinned by SHA-256 from the original Real-ESRGAN release) and tests them on the GPU. Existing installations ask for Install / verify once, which fetches only what is new.
- Storage counts the upscaler with the image models, and removing Stable Diffusion 1.5 removes it too.

## 0.2.0-alpha.3 — 2026-10-17

- Settings has a Storage section: free space, and the size of the runtime, model, download caches, images, logs, and job records in the saved data folder.
- **Clear…** removes the download caches (about 4 GiB after setup) or logs; **Remove Stable Diffusion 1.5…** removes the runtime, model, and caches while keeping images and their records. Each asks for confirmation and waits for any running operation.
- The developer CLI gains a read-only `storage` command.
- Image from an image: start from your own picture (Choose a picture…, drag and drop, or **Use as starting picture** on any Library image or the latest result), describe what it should become, and choose how much to change it (a little, moderately, a lot). The result keeps the picture's proportions. It uses the installed Stable Diffusion 1.5 model, so nothing new downloads; the job works from a private copy of the picture and never records the original's location.
- Updates: when online, the app checks GitHub Releases at startup (can be turned off in Settings) and on request from Home. A newer version is shown with its release notes; **Update now** downloads the installer, verifies it against the published SHA-256, then closes the app, installs silently, and reopens it. Nothing installs without that click, and offline use is unaffected. Pre-release installs are offered pre-releases; stable installs only stable releases.
- The title bar shows the actual installed version.
- Media Workbench is now released under the MIT License; the installed app includes `LICENSE.txt`.
- Create is simpler: once Stable Diffusion 1.5 is verified, setup shrinks to one status line, and the main path is a description, things to avoid, a Square/Portrait/Landscape shape (512 × 512, 512 × 768, 768 × 512), and Generate.
- A collapsed **Advanced options** section holds seed, refinement steps, prompt strength (guidance 1–20), and sampling method (balanced PNDM default, crisp-detail DPM++ 2M Karras, or softer Euler ancestral), with a plain explanation. Older job records read as 512 × 512, guidance 7.5, PNDM; **Use settings** restores all of them.

## 0.2.0-alpha.2 — 2026-10-17

- Settings edits a draft data folder; install, generation, model status, Library, and Open folder keep using the saved folder until **Save settings** succeeds, and typing no longer clears readiness or loaded images.
- Generation no longer re-hashes the 2.55 GiB model each run. Full SHA-256 verification still runs at install / verify, which records each file's size and modification time; generation and status compare against those and require re-verification on any change.
- Readiness is tied to an explicit adapter version plus the pinned profile, lock, manifest, and recipe (line-ending independent), not to the worker source files.
- Setup and the inference child apply one VRAM limit, read from the recipe's `minimumVramMiB`, to the same GPU (`CUDA_DEVICE_ORDER=PCI_BUS_ID`, honouring `CUDA_VISIBLE_DEVICES`).
- An unreadable nvidia-smi memory value such as `[N/A]` is reported as a setup failure with a clear message instead of invalid input.
- New generation records store the image path relative to `artifacts/`; Library resolves paths before checking containment, rejects `..` escapes, and still lists older absolute-path records after the data folder moves.
- The Python coordinator runs the recipe through the Rust installation gate itself (via the C ABI), so no client of the worker can skip it; a blocked recipe never starts a setup attempt.
- Re-verifying an installation whose runtime is already synced needs free space only for missing model files plus 2 GiB, instead of a flat 20 GiB.
- A second launch brings forward the running copy's own window, found through its process, rather than any window titled "Media Workbench"; a minimized window is restored and a maximized one stays maximized.
- Release builds ignore `MEDIA_WORKBENCH_PYTHON`; only Debug builds allow overriding the bundled interpreter.
- Tests validate bundled recipes and live worker messages against `schemas/`, and a repository-hygiene test rejects personal paths, private email addresses, and built binaries in tracked files. The unused C# `JobLifecycle` contract was removed.
- Library lists recent failed, cancelled, and interrupted generations with their reason, a **Use settings** button that loads them into Create, and **Open log** for that generation's own inference log. Failures no longer push older images out of the 200-image listing.
- Library images use a virtualized grid and decode at thumbnail size.
- The main view model moved into a UI-free `MediaWorkbench.Presentation` library that the C# checks load directly.
- CI publishes the installer that the checked build produced instead of rebuilding it in the release job, and no longer runs the published-app check twice.

Existing ready installations need **Install / verify** once more because the readiness stamp changed.

## 0.2.0-alpha.1 — 2026-10-17

- First SD 1.5 setup and prompt-to-image pipeline, with a private Python 3.12/CUDA runtime, exact package lock, pinned Hub commit, and selected safetensors files.
- Native plan gate, storage preflight, model checksum verification, and a real local GPU inference test before ready state.
- Persistent setup attempts, interruption detection, retained downloads on retry, streamed progress, and owned-child cancellation.
- Create controls for prompt/negative prompt, seed, and inference steps; PNG preview and saved settings/device/output evidence.
- Library reads recent successful image jobs; automatic model status/library refresh when opening those pages.
- Additional setup/recovery/cancellation checks and published-payload requirements for inference files.
- Each worker runs in a kill-on-close Windows Job Object, so a forced stop, worker failure, or app crash also ends the inference child and other descendants.
- Generation jobs left running by a stopped owner are recorded as interrupted, partial images and unfinished records are removed, damaged records no longer block Library, and Library reports recovered jobs.

Experimental Windows x64/NVIDIA profile, 6 GB VRAM minimum, 512 × 512 FP32 offload. Protected Hub access, arbitrary model installation, image editing, and video remain planned. Installer unsigned; clean Windows 10 verification pending.

## 0.1.0-alpha.1 — 2026-10-17

First installable development alpha of Media Workbench.

- Dark WinUI 3 MVVM shell using C# 14 and .NET 10.
- Rust native core behind a documented C ABI: installation planning, Windows relative-path policy, and streaming SHA-256.
- Bundled CPython discovery worker, live public Hugging Face search, task filters, and model details.
- Storage settings, component diagnostics, operation cancellation, and session activity.
- Per-user Windows installer with Start menu integration, custom icon, stable taskbar identity, and single-instance launch.
- Source architecture documents, native/worker boundary checks, CI, and packaged resource/runtime smoke tests.

Model installation, inference environment provisioning, image/video generation, account access, and durable media history remain in development. This alpha downloads no model weights. The installer is unsigned; Windows 10 clean-machine verification is pending.
