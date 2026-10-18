# Changelog

## 0.3.0-alpha.9 — 2026-10-18

- **A new picture every time you select Generate.** Before, the seed stayed the same, so selecting **Generate** again with the same description made exactly the same picture. Now each **Generate** and **Add to queue** picks a new seed and shows it under **Advanced options**, where **A new variation each time** can be turned off. **Use settings** and **Retry** turn it off for you, so they repeat a picture exactly.
- **Add to the queue while pictures are being made.** **Add to queue** now works while a picture or the queue is being made, and a running queue also makes the pictures added meanwhile. It stays unavailable during setup, moving your data and similar work.

## 0.3.0-alpha.8 — 2026-10-18

- **A queue of pictures to make later.** Create's **Add to queue** saves the picture you described (or each version, with **How many**) without making it yet. Change the description and add more, then select **Run queue** and Media Workbench makes them one after another, reusing the loaded model. Waiting pictures keep their own copies of any starting picture or painted area, stay waiting after you close the app, and only run when you choose **Run queue**. **Cancel** stops the picture being made and leaves it and the rest waiting; a picture that fails is listed in Library with **Retry**, and the others are still made. **Remove** takes one out of the queue.

## 0.3.0-alpha.7 — 2026-10-18

- **Add Stable Diffusion XL fine-tunes.** Once Stable Diffusion XL is set up, **Add a model you already downloaded** and Discover's **Download and test** accept single-file SDXL checkpoints (`.safetensors`), such as fine-tuned SDXL models. Media Workbench reads only the file's header to recognize it, copies it into your data folder, and makes a real 1024 × 1024 test picture on your graphics card; only a model that passes appears under **Model**, with SDXL's picture shapes. SDXL refiners, SDXL inpainting models and SDXL model folders are explained and refused.
- **Make several versions at once.** Create's **How many** makes 2 or 4 versions of a picture in one go, each with the next seed, so every version can be made again with **Use settings**. After the first, each version reuses the loaded model. If one version is rejected (for example by the safety checker), the others are still made and the app says which seed was skipped.

## 0.3.0-alpha.6 — 2026-10-18

- **Changing a picture is about four times faster on 6 GB graphics cards.** Starting from a picture and painted changes left part of the model on the graphics card while the rest worked, so Windows had to borrow system memory and every step slowed down. That part now steps aside first. On a GTX 1660 Ti, a 12-step change of a 512 × 512 picture went from 48 to 11 seconds and a painted change from 77 to 16 seconds. The pictures are exactly the same as before, byte for byte.
- This applies to the memory-saving settings used on 6 GB cards and on cards without reliable half precision, for Stable Diffusion 1.5, models you added, and Stable Diffusion XL's faster settings. Existing installations stay verified.

## 0.3.0-alpha.5 — 2026-10-18

- **Faster repeated pictures.** After a picture, Media Workbench keeps the model loaded for two minutes, so the next picture with the same model and mode starts straight away. On a GTX 1660 Ti, 12-step pictures went from about 25 seconds to about 16. The pictures are exactly the same as before: a check on that card compares them byte for byte. Create says when the model was still loaded from the last picture.
- While the model stays loaded, Media Workbench keeps the data folder to itself, so another program, such as a local MCP client, cannot load a second model onto the same graphics card; it is told why and that the model is released after two idle minutes. Setup, adding or removing models, clearing storage, moving the data folder, upscaling and videos release it first; it is also released when you switch data folders or close the app.
- An unattended installation (`/VERYSILENT`) no longer opens Media Workbench afterwards. In-app updates still reopen the updated app.

## 0.3.0-alpha.4 — 2026-10-18

- **Choose the drive before the big download.** Settings lists the drives on this computer with their free space and type, and which one holds Windows. **Use this drive** fills in a folder there; you still choose whether to save it or move your data. If the data folder is still the default on a drive with less room than another, Create asks where Media Workbench should work before setup's first download, and waits until you keep the drive or choose another. Your choice is remembered.
- **Never use a drive.** Mark a drive **Never use this drive** in Settings, and Media Workbench will not save or move its data there or suggest it. Settings warns if the current data folder is on such a drive.
- **Remove the old copy after a move.** After moving your data, Settings offers **Remove the old copy…**, also after a restart. It deletes only the files the new folder holds unchanged; anything that differs or was added later stays, and the app says how many files were kept.

## 0.3.0-alpha.3 — 2026-10-18

- **Move your data to another folder or drive.** Type a new folder in Settings and select **Move my data there…**. Media Workbench first says how much it will copy, how much room the new drive has, and anything that prevents the move (the folder is not empty, there is not enough space, or the new folder is inside the current one). **Move now** copies your pictures, videos, models and setup, checks every copied file against the original, and only then switches to the new folder, so nothing has to download or set up again.
- Your current folder is never changed by a move. Once you have looked at the new one, you can delete the old folder yourself in File Explorer.
- If the move fails, is cancelled, or the computer stops part-way, Media Workbench keeps using the current folder and removes the partial copy (at the next start, after a crash). Temporary work and the Library's search index are not copied; Library rebuilds its index in the new folder.
- Files keep their dates when moved, so Stable Diffusion 1.5, SDXL, videos and models you added stay verified. If you use the local MCP host, update its `--data-root` after moving.

## 0.3.0-alpha.2 — 2026-10-18

- **Temporary work stays in your data folder.** Setup, pictures, videos and upscaling keep their temporary files in the data folder's `temp` folder instead of Windows' temporary folder on the system drive, and remove them when each job ends. Package unpacking during setup and the graphics card's compiled-kernel cache go there too. This is the first part of the drive-containment work planned for 0.4.0-alpha.1.
- Painted areas being handed to a job, and downloaded app updates, also go into the data folder. An update download first checks that the drive will keep at least 512 MB free.
- If the data folder's drive is disconnected, full or read-only, Media Workbench stops with a plain explanation and writes nothing anywhere else. Reading this computer's hardware or searching Hugging Face still never creates a data folder that does not exist yet.
- Settings has a new **Where Media Workbench keeps things** list: the data folder, temporary work, caches, preferences and the app itself, each with the drive it is really on (mounted folders included), and one sentence saying what, if anything, is on another drive. Storage lists **Temporary work** as its own area.
- Temporary folders left by a crash are removed at the next start, while a job that is still running keeps its own. Leftovers from earlier versions in Windows' temporary folder are still cleaned up.
- Still on the system drive: preferences and the startup log (about 2 MB at most), the app installation, and the installer's own unpacking. Changing the data folder still does not move existing files. Inference itself is unchanged; existing installations and added models stay verified.

## 0.3.0-alpha.1 — 2026-10-18

- **Short videos.** Create has a new **What to make → A short video** choice that makes 2- or 3-second clips (16 or 24 frames) with Stable Diffusion 1.5 and the AnimateDiff motion adapter. **Set up videos** downloads the pinned adapter (1.7 GB, Apache-2.0), verifies it, and makes a real test clip on your graphics card; nothing can be made until that passes. Videos are saved as MP4 with Windows' own H.264 encoder, play on a loop in Create, show their first frame in Library (**Videos** filter, **Play video**, **Save a copy…** as a verified MP4), and every frame is reviewed by the model's safety checker.
- **Stable Diffusion XL.** Once Stable Diffusion 1.5 is ready, **Get Stable Diffusion XL** sets up SDXL base 1.0 (6.9 GB, CreativeML Open RAIL++-M) with its own download, verification and 1024 × 1024 GPU test. Then **Model** offers it, with 1024-pixel square, portrait and landscape shapes; starting pictures and painted changes work with it too.
- Both choose their settings from your hardware like pictures do. On 6 GB graphics cards, or cards without reliable half precision, the model streams to the card layer by layer: it fits, but a video or an SDXL picture can take several minutes, and full-precision SDXL needs 16 GB of system memory (the app says so before downloading). Faster settings are offered on larger cards and must pass the same test first.
- Settings can remove the video setup or Stable Diffusion XL again; your videos and pictures are kept. Clean-up and crash recovery remove raw video frames and partial files. The local MCP tools gain video status, setup and generation, SDXL setup, and SDXL generation.
- The Rust core encodes video through Media Foundation loaded at run time, so the app still starts on Windows editions without it; those are told to add the Media Feature Pack. Existing Stable Diffusion 1.5 installations and added models stay verified.
## 0.2.0-alpha.16 — 2026-10-18

- Create now says how long loading the model took, separately from the seconds spent making the picture. Job records keep the runtime start, model-load and inference times apart, so repeated-generation work can be measured before a warm worker is built.
- When loading takes 20 seconds or more, Create says why it was probably slow: a data folder on a hard disk, USB drive or network drive is named, with the advice to move it to a solid-state drive; on a solid-state drive, a load far slower than the drive allows is described as the drive or the computer being busy at the time.
- The Library and the local MCP tools return the model-load time and any slow-load note with each image; older records without them still list normally, and damaged timing fields cannot hide other images. Upscaling records its load time too.
- Inference settings, adapter versions and readiness stamps do not change; existing installations and added models stay verified. The thresholds come from single-GPU observations and have not yet been compared against a real hard-disk data folder.

## 0.2.0-alpha.15 — 2026-10-18

- Model compatibility decisions now live in the Rust core. Discover downloads, local single-file imports, and Diffusers-folder imports share the same SD 1.5 tensor-layout checks and plain explanations for unsupported models, add-ons, and compressed weights.
- Copied Diffusers parts now pass the same compatibility checks again before inference testing, including weight formats. Missing or incompatible native services stop the check; there is no separate Python compatibility policy to drift out of sync.
- Existing verified models remain ready. Supported architectures, runtime settings, and the actual file/runtime/GPU verification gates are unchanged; this milestone moves compatibility policy without changing inference.
- Expanded native, Python, managed-boundary and published-application checks, and recorded headless-first development and frequent handoff expectations for future sessions.

## 0.2.0-alpha.14 — 2026-10-18

- Discover can now download and test public single-file Stable Diffusion 1.5 models. **Check model** reads a small file header, checks this computer and the saved data folder's free space, and explains what prevents a download. Search results show license metadata and a hint about available model files; newer architectures, add-ons, and video still need their own adapters.
- **Download and test** uses the exact repository revision, file size, and checksum shown by the check. It requires a model-card/license review, shares the already verified Stable Diffusion 1.5 runtime, verifies the downloaded file through Rust, and runs the real GPU test before selecting the model in Create. Another model file can be chosen under **Advanced options**.
- Downloads keep one owned copy, check space again during the transfer, report progress as network bytes arrive, and remove unfinished or failed additions, including after a crash. Pinned model cards, licenses, and notices are retained; generation records keep the repository, revision, file checksum, and license metadata. No repository code or configuration is used.
- Rust owns the bounded public-download metadata policy. Protected downloads, missing licenses or immutable revisions, unsafe paths, unverified hashes, individual model parts, and unsupported file headers cannot become a ready model. Choosing another repository, file, or data folder invalidates an earlier check.
- Existing installations and local imports remain verified without a new setup test. Downloading Diffusers folders directly from Discover and protected Hub access remain planned; local Diffusers-folder import still works.

## 0.2.0-alpha.13 — 2026-10-18

- Settings chosen for your computer: Media Workbench now reads your graphics card, its memory, your system memory, and the data folder's drive, and shows them in plain words on Create's setup card and in **Diagnostics**, with why its settings were chosen. Nothing new is installed to do this.
- How pictures are made now follows that hardware: half precision on graphics cards that handle it well, the whole model on the graphics card when it fits, and otherwise the memory-saving settings used until now (full precision, parts of the model waiting in system memory). GTX 16-series cards keep the memory-saving settings, because half precision makes blank pictures on them.
- Every choice must pass the real test on your GPU during **Install / verify**. If faster settings fail it, the memory-saving settings are tested and used, and that card and driver are not asked to try the faster ones again. Because the settings are now part of what "verified" means, existing installations and added models ask for **Install / verify** (or **Test again**) once after updating; nothing is downloaded again.
- Computers with less than 8 GB of memory, or a graphics card below 6 GB, are told so plainly before anything downloads; with less than 16 GB, the setup card and Diagnostics warn that memory-saving settings may be slow. If you change graphics cards, Create says when faster settings can be tested or when the old ones must be tested again.
- Upscaling uses larger tiles when more graphics memory is free, and smaller ones (instead of failing) when memory runs short.
- The complete real-hardware check passed with memory-saving settings on a GTX 1660 Ti (6 GB), including generation and a 4× upscale. Faster runtime choices still need representative RTX/Pascal hardware checks; simulated hardware tests do not certify them.

- Drive information now comes from one Rust service for both the hardware profile and the desktop, including quota-aware free space. Diagnostics stops calling settings verified if runtime/model files are missing or changed, or the current card cannot use those settings.
- Development builds now use central NuGet versions, committed dependency lockfiles with locked CI restores, a narrower .NET SDK pin, explicit x64 host checks, and app-local Rust DLL loading. Published checks inspect executable architectures. The stack audit and handoff document the remaining release evidence and deliberate exceptions.

## 0.2.0-alpha.12 — 2026-10-17

- Media Workbench now cleans up after itself, at startup and after setup, without asking: unfinished-job records beyond the 50 that Library lists (with their private input copies and logs), logs older than 30 days or beyond the newest 300, setup downloads left unfinished for three days, painted-area hand-over files a crash left behind, and update installers once the new version runs. Images, finished job records, anything Library lists for a retry or **Use settings**, and your own files are never removed.
- Once setup is verified, its download caches (about 4 GB) are removed automatically. Settings → Storage explains this and has **Keep setup's download files for repairs** for anyone who would rather keep them.
- Generation and upscaling check free space before writing anything and stop with a plain message when the drive is too full. Create, Tools, and Settings warn when less than 5 GiB is free on the data folder's drive.
- When the data folder is on a hard disk, USB drive, or network drive, Create and Settings say that models load faster from a solid-state drive and that the folder can be moved in Settings.
- Rust owns the clean-up rules and drive detection (`mw_tidy_data_folder`, `mw_drive_info`, and a new busy status code). Clean-up runs under the same data-folder lock as setup and generation, finishes an interrupted removal on the next start, and never follows links.

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
