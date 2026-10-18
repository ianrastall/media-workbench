# Changelog

## 0.2.0-alpha.11 — 2026-10-17

- Add a model you already downloaded: once Stable Diffusion 1.5 is ready, open **Add a model you already downloaded** in Create and choose a Stable Diffusion 1.5-family `.safetensors` file or a Diffusers model folder. Media Workbench checks it from its file headers alone, without running any model code, and explains plainly when it cannot be used (SDXL, SD 2, LoRAs, embeddings, inpainting-only models, damaged or incomplete files, and `.ckpt` files, which can run hidden code).
- The model is copied into your data folder, hashed while copying, verified, and tested with a small picture on your GPU, the same check the bundled model passes. Your original is never changed, and nothing is kept if a step fails. It reuses the installed runtime, so nothing downloads.
- Create shows a **Model** choice once an added model passes its test. Image-to-image, painted changes, and upscaling keep working. Library notes which model made each image, and **Use settings** chooses that model again.
- Settings → Storage measures models you added separately and lists each one, with **Test again** and **Remove…** (your images and original files are kept). Removing Stable Diffusion 1.5 keeps added models for when it is set up again.
- MCP: `workbench_models` lists models and their tested state; `workbench_generate_image` can name a ready added model. Adding and removing models stays in the app.

## 0.2.0-alpha.10 — 2026-10-17

- Change part of a picture: after choosing a starting picture in Create, select **Paint the part to change**, paint over it (small, medium, or large brush; erase; clear), describe what should be there, and choose how much to change it (a little, moderately, or completely). Only the painted part is redrawn; everything else keeps its pixels and the picture keeps its full size, up to 4096 pixels on the longest side.
- The painted area is redrawn from a close-up with surrounding context, so small areas of large pictures get full detail, then blended in with a softened edge. It uses the installed Stable Diffusion 1.5 model, so nothing new downloads and existing verified installations stay ready.
- Each job keeps private copies of its picture and painted area. **Use settings** in Library restores both, with the change level. Choosing another picture discards the painting; **Change the whole picture** returns to ordinary image-to-image.
- MCP: `workbench_generate_image` accepts a rectangular change area (`changeLeft`, `changeTop`, `changeWidth`, `changeHeight` as fractions of a Library source image) for clients that cannot paint.

## 0.2.0-alpha.9 — 2026-10-17

- Rust now verifies the pinned SD 1.5 and Real-ESRGAN files during Install / verify: safe contained paths, expected sizes, streamed SHA-256, unchanged metadata, and read-only file sharing on Windows. Hashing reports byte progress and checks cancellation every 64 KiB, so a large model file no longer has to finish hashing before cancellation is observed. Downloads are retained for retry.
- The setup worker uses a thin native adapter and keeps the existing readiness stamps. Files, runtime imports, and actual GPU inference must still pass before a model becomes ready; this release does not change the inference profile or invalidate existing verified installations.
- Published-app self-tests now exercise the shipped file verifier. The smoke-test harness allows startup/report time beyond the application's operation deadline, avoids a process-exit race on timeout, and accepts a separate report folder.

- Source builds copy the MCP console host beside the GUI, so Settings-generated LLM client configuration also works with the normal development build.

## 0.2.0-alpha.8 — 2026-10-17

- Rust now owns storage inventory: folder traversal, per-area bytes/file counts, installation totals, and available disk space. Settings, the developer CLI, and MCP call it directly without starting Python; worker clients use a thin native adapter.
- Storage measurement checks cancellation between entries, rejects linked/reparse-point managed roots, skips links inside trees, and reports an error rather than partial totals on access failures. Sizes are logical file sizes; only the bundled model/runtime profiles are counted as installation components.
- Native artifact export and storage inventory share filesystem path checks. Published-payload tests now exercise native storage through both the app and the shipped MCP host. Existing model readiness and confirmed storage-removal behavior are preserved.

## 0.2.0-alpha.7 — 2026-10-17

- Release documentation is generated from the version, changelog, and current README. Every release includes installation steps, runtime/hardware requirements, supported features, MCP setup guidance, limitations, license links, and checksum instructions in its release body and a downloadable README asset alongside the installer and checksum.
- Builds refresh the main README's current-release summary and download links; checks block publication if it is stale. Documentation links in release notes are pinned to that release's source tag.
- The public repository now has a descriptive summary and topics for Windows, Rust, local AI image generation/upscaling, WinUI 3, and MCP discovery.

## 0.2.0-alpha.6 — 2026-10-17

- LLM tool access: an opt-in local MCP stdio host lets compatible desktop LLM clients inspect model readiness/storage/Library, read native plans, and search public Hub metadata. Settings → **Connect an LLM client (MCP)** generates and copies the client configuration for the saved data folder.
- Separate launch-time permissions enable SD 1.5/upscaler install/verify and verified image generation/upscaling. The model cannot enable its own permissions, change the selected folder, execute arbitrary commands, or install arbitrary models. Image-to-image/upscaling inputs come from completed Library jobs. All normal readiness, cancellation, process ownership, and durable-job checks still apply.
- The installer bundles the self-contained console host; the client handles its own LLM account. This first transport is local stdio; authenticated Internet access and video are still planned.
- Published-payload checks exercise the shipped MCP host, its default permissions, and a real bundled worker session.
- Data-folder locks no longer write/flush a sentinel byte before attempting the lock. Empty-file byte-range locking keeps the same owner exclusion while avoiding unnecessary writes and a first-owner initialization race.

## 0.2.0-alpha.5 — 2026-10-17

- **Save a copy…** on a Library image or the latest Create/Tools result opens a Windows save picker and exports the original PNG to your chosen folder. The copy must match its recorded SHA-256 before it replaces the destination; failed or cancelled copies keep an existing destination intact. Copies go outside the managed data folder so originals and job records cannot be overwritten.
- Native core expansion: Rust now owns the complete verified export transaction, including completed-job parsing, containment/reparse-point checks, cancellable streaming/hash verification, and atomic file replacement. The GUI calls it directly through the C ABI; Python provides a thin adapter for worker clients.
- **Open image** opens a Library image or latest result in your default PNG viewer.

## 0.2.0-alpha.4 — 2026-10-17

- Library searches prompts, things to avoid, seeds, and job IDs, with filters for generated or upscaled images. **Search / refresh** (or Enter in the search box) applies the filters; **Older images** and **Newer images** browse 48 images at a time, with a matching-image count. Images beyond the previous 200-image limit are now accessible.
- Library keeps a local SQLite index derived from the existing job JSON. Refresh reads changed records, checks image paths, and reflects removed or restored files. A missing, corrupt, or incompatible index rebuilds automatically; your images and records stay in place. Storage reports its size separately.
- **Use settings** on a successful Library image restores the prompt, seed, size, sampler, and private starting picture in Create. Upscaled images and unfinished upscales restore their original picture and scale in Tools. Unfinished jobs remain visible independently of the image search.

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
