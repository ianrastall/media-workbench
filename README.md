# Media Workbench

A dark Windows desktop workspace for local image and video tools, built with **WinUI 3, MVVM, C# 14, .NET 10, a Rust core exposed through a C ABI, and out-of-process Python workers**.

**Status: experimental alpha — local image creation, upscaling, and a searchable Library.** Discover public Hugging Face models, install an isolated Stable Diffusion 1.5 environment, verify your GPU, generate images from prompts, and add compatible SD 1.5-family models you already downloaded. Video generation remains planned.

<!-- release-summary:start -->
## Current release

**[0.2.0-alpha.14](https://github.com/ianrastall/media-workbench/releases/tag/v0.2.0-alpha.14)** · 2026-10-18 · Pre-release · Windows x64

[Installer](https://github.com/ianrastall/media-workbench/releases/download/v0.2.0-alpha.14/MediaWorkbench-0.2.0-alpha.14-win-x64-setup.exe) · [SHA-256](https://github.com/ianrastall/media-workbench/releases/download/v0.2.0-alpha.14/MediaWorkbench-0.2.0-alpha.14-win-x64-setup.exe.sha256) · [Release README](https://github.com/ianrastall/media-workbench/releases/download/v0.2.0-alpha.14/MediaWorkbench-0.2.0-alpha.14-README.md)

- Discover can now download and test public single-file Stable Diffusion 1.5 models. **Check model** reads a small file header, checks this computer and the saved data folder's free space, and explains what prevents a download. Search results show license metadata and a hint about available model files; newer architectures, add-ons, and video still need their own adapters.
- **Download and test** uses the exact repository revision, file size, and checksum shown by the check. It requires a model-card/license review, shares the already verified Stable Diffusion 1.5 runtime, verifies the downloaded file through Rust, and runs the real GPU test before selecting the model in Create. Another model file can be chosen under **Advanced options**.
- Downloads keep one owned copy, check space again during the transfer, report progress as network bytes arrive, and remove unfinished or failed additions, including after a crash. Pinned model cards, licenses, and notices are retained; generation records keep the repository, revision, file checksum, and license metadata. No repository code or configuration is used.
- Rust owns the bounded public-download metadata policy. Protected downloads, missing licenses or immutable revisions, unsafe paths, unverified hashes, individual model parts, and unsupported file headers cannot become a ready model. Choosing another repository, file, or data folder invalidates an earlier check.
- Existing installations and local imports remain verified without a new setup test. Downloading Diffusers folders directly from Discover and protected Hub access remain planned; local Diffusers-folder import still works.
<!-- release-summary:end -->

## Install

Download the Windows x64 installer from [Releases](https://github.com/ianrastall/media-workbench/releases). The installer adds Media Workbench to your Start menu and installed applications. The app has a stable Windows application identity and can be pinned to the taskbar normally. It does not change your existing taskbar pins.

Target: **Windows 10 22H2 or Windows 11, x64**. This alpha has been tested on Windows 11; Windows 10 clean-machine verification is still pending. The installer is currently unsigned.

The GUI, .NET/Windows App SDK runtime, Rust library, and private discovery Python runtime are bundled. You do not need system Python or .NET. Internet access is required for discovery and first-time model setup. Inference setup downloads its own Python 3.12, CUDA PyTorch, and locked packages into your chosen data folder.

For image generation, this first profile requires an **NVIDIA GPU with at least 6 GB VRAM**, a compatible driver, and **20 GiB free space** during first-time setup (re-verifying an existing installation needs space only for missing files). It produces 512 × 512, 512 × 768, or 768 × 512 images, with a choice of guidance and three samplers. Other GPU vendors and CPU-only generation are not yet supported.

**Settings chosen for your computer:** Media Workbench reads your graphics card, its memory, your system memory, and the data folder's drive, and shows them in plain words on Create's setup card and in **Diagnostics**. From them it chooses how pictures are made: half precision on graphics cards that handle it well, the whole model on the graphics card when it fits, and otherwise the memory-saving settings (full precision, with parts of the model waiting in system memory). GTX 16-series cards keep the memory-saving settings, because half precision makes blank pictures on them. Whatever is chosen must pass the real test on your GPU during **Install / verify**; if faster settings fail it, the memory-saving ones are tested and used instead. Computers with less than 8 GB of memory or a graphics card below 6 GB are told so before anything downloads, and computers with less than 16 GB are warned that memory-saving settings may be slow. Upscaling uses larger tiles when more graphics memory is free. So far only the memory-saving settings have passed on real hardware (a GTX 1660 Ti); the faster settings are checked by the same test on each computer that chooses them.

1. Open **Settings**, choose a data folder on a drive with enough space, and save.
2. Open **Create**, review the linked model card/license, and select **Install / verify**.
3. Setup downloads roughly 5 GiB plus dependencies and tests real inference. A model becomes ready only after the checks pass on your machine.
4. Describe the picture, pick a shape, and select **Generate image**. To start from a picture of your own instead, choose or drag it onto Create (or select **Use as starting picture** on a Library image) and say how much to change it. To change only one part of it, select **Paint the part to change**, paint over that part, and describe what should be there; everything you don't paint stays exactly as it was, at the picture's full size (seed, steps, prompt strength, and sampling method are under **Advanced options**). Your PNG and its settings appear in **Library** and remain in your data folder. Generations that fail, are cancelled, or are stopped by closing the app are listed under **Unfinished generations** with the reason; **Use settings** loads them back into Create and **Open log** shows the inference log.

**Library** searches prompts, things to avoid, seeds, and job IDs. Choose an image type and select **Search / refresh** (or press Enter in the search box), then browse with **Older images** / **Newer images**, 48 at a time. **Use settings** restores a saved image's original controls in Create or Tools, including its private starting picture and any painted area. Your JSON records and pictures remain authoritative; the local SQLite index rebuilds automatically if missing or damaged. Recent unfinished jobs are shown independently of your image filters. **Open image** uses your default PNG viewer; **Save a copy…** exports a checksum-verified copy through the Windows save picker (also available on the latest Create/Tools result). Choose a folder outside the managed data folder; prompts and job records stay with the original in Library.

**Add a model you already downloaded:** once Stable Diffusion 1.5 is ready, open **Add a model you already downloaded** in Create and choose a Stable Diffusion 1.5-family `.safetensors` file (for example a fine-tuned checkpoint) or a Diffusers model folder. Media Workbench reads only the file headers to check that it is an SD 1.5-family model and explains plainly when it is not (SDXL, SD 2, LoRAs, embeddings, inpainting-only models, and older `.ckpt` files, which can run hidden code, are refused). It copies the model into your data folder — your original stays where it is, and you may delete it afterwards to save space — records its SHA-256, verifies the copy, and makes a small test picture on your GPU. Only a model that passes appears in Create's **Model** choice; it reuses the installed runtime, so nothing downloads. Library notes which model made each image and **Use settings** chooses it again. Follow the license on the page you downloaded the model from.

**Tools** makes any picture 2× or 4× bigger and sharper, offline. Choose or drag a picture there, or select **Make it bigger** on a Library image.

During **Install / verify**, Rust checks every pinned model file's size and SHA-256 with byte progress and cancellation during large-file hashing. Cancelling keeps downloaded files for retry. File checks alone never mark a model ready; runtime and GPU inference checks must also pass.

**Updates:** when you're online, Media Workbench checks for a new version at startup and shows what changed on **Home**. Select **Update now** to download it, verify it against its published checksum, and reopen the updated app; nothing installs until you click. After setup, generating images needs no internet.

**Settings → Storage** shows what the data folder uses. Rust measures logical file sizes and available space directly, without starting Python, and checks cancellation while traversing folders. You can clear the download caches and logs, or remove Stable Diffusion 1.5 entirely; your images and their records are always kept. Models you added are listed with their size and state; **Test again** re-checks one, and **Remove…** deletes its copy (never your original file or your images).

**Media Workbench cleans up after itself.** Once setup passes its GPU test, it removes the packages and archives it downloaded (about 4 GiB; a repair downloads them again), unless you tick **Keep setup's download files for repairs** in Settings. At each start it removes unfinished downloads older than three days, logs beyond the newest 300 or older than 30 days, failed or stopped jobs beyond the 50 Library lists (with their private picture copies), painted-area hand-over files a crash left in the temporary folder, and installers of an update that has finished. It never removes images, the records Library lists, the pictures **Use settings** needs, the log of a job still listed as unfinished, or your own files, and it waits rather than run while setup or generation is using the data folder.

**Drive space and speed:** generating and upscaling check free space before writing anything and say plainly when there is not enough (a 4× upscale can need a few hundred MB). Create, Tools, and Settings warn when the data folder's drive has less than 5 GiB free. When the data folder is on a hard disk, USB drive, or network drive, Create and Settings say that models load faster from a solid-state drive and that the data folder can be moved in Settings.

Cancel stops the owned worker operation. Retrying setup reuses completed downloads and the Hub cache (unfinished downloads are kept for three days); an incomplete bootstrap archive restarts. A data folder typed in Settings takes effect only after **Save settings**. Changing data folders does not move existing models or images.

Developer architecture and remaining acceptance work are recorded in [stack alignment](docs/stack-alignment.md) and [handoff](HANDOFF.md). Dependency versions are centralized and locked in CI; native hosts and published payloads are checked for x64, and the Rust core loads from beside the application.

**Download a model from Discover:** search the public Hub, select a result, and choose **Check model**. The app reads a bounded header to identify an SD 1.5-family single-file model and checks the graphics card, system memory, and data folder's free space. Review the pinned model card/license, tick the review box, and choose **Download and test**. Stable Diffusion 1.5 must already be verified in Create because added models share its private runtime. Only a file that passes native size/checksum checks and real GPU inference becomes available and is selected in Create. Search metadata is a hint; it never proves compatibility. **Advanced options** offers other files at the same immutable revision. Direct downloads of Diffusers folders and protected repositories are not supported yet; local folder import remains available.

The download keeps one copy under the managed models folder, preserves the pinned model card/license/notice files, checks free space during the transfer, and removes incomplete or failed additions. A crash leftover is recovered at the next idle operation. Cancelled downloads restart rather than resume. Images and job records preserve the pinned source; removing a downloaded model in Settings keeps those images and records.

## What works now

- Native dark WinUI shell with MVVM commands and observable state.
- Live public Hugging Face search with task filters, license metadata and file hints; bounded header/hardware/space checks and pinned download-and-test for public SD 1.5 single-file checkpoints.
- Model details: publisher/repository, revision, task, library, license metadata, access state, and repository files.
- Rust installation-plan validation, relative-path policy, and streaming SHA-256 through C ABI 1.
- Rust-owned completed-job validation and cancellable, checksum-verified PNG export with atomic replacement.
- Rust-owned cancellable storage inventory, shared by desktop, MCP, CLI, and worker clients.
- Rust-owned automatic clean-up of logs, old unfinished jobs, stale downloads, and verified setups' download caches, under the data-folder lock; free-space checks before generation and upscaling; low-space and hard-disk notices.
- Rust-owned pinned model-file verification with byte progress, cancellation, and protection against concurrent writes while hashing.
- Bundled Python worker diagnostics and a plain-language hardware profile (GPU, graphics and system memory, data drive) that chooses verified inference settings.
- Persistent storage settings, cancellation, and session activity.
- Pinned SD 1.5 model files with SHA-256 checks and an exact, hash-locked inference dependency set.
- Private runtime provisioning, persistent installation attempts, progress, retry, and a real GPU verification gate.
- Worker process trees owned by a Windows Job Object: closing or crashing the app stops setup and inference children, and unfinished generations are recorded as interrupted.
- Change only a painted part of a picture (inpainting) with the installed SD 1.5 model: the painted area is redrawn from a close-up and blended back into the full-size original.
- Add SD 1.5-family models you already downloaded (single `.safetensors` files or Diffusers folders): identified from safetensors headers without running model code, copied and hashed by Rust, and usable only after the same real GPU test as the bundled model.
- Prompt/negative prompt, deterministic seed, 1–50 inference steps, local PNG output, preview, and saved artifact history.
- Per-user install/uninstall, Start menu shortcut, custom icon, taskbar identity, and single-instance behavior.

Discovery results do not imply installation support: **only the bundled SD 1.5 recipe, SD 1.5-family models you add from your own files after they pass their GPU test, and the Real-ESRGAN upscaler are currently executable**. Installing from Discover, other model architectures (SDXL and newer), LoRAs, protected Hugging Face access, arbitrary model installation, additional enhancement tools, a dedicated inpainting model, and video pipelines remain in development. Model licensing is separate from the application license.

## LLM clients

Settings → **Connect an LLM client (MCP)** generates configuration for a compatible desktop client's local MCP server settings. The installer includes the console host and runtime; no system Python/.NET or provider API key is needed by Media Workbench. Your LLM account stays in the client. Inspection is enabled by default; you can include separate launch-time permissions for model setup and image generation/upscaling. The model cannot turn these permissions on itself or change the selected data folder. It uses the normal native installation plan and per-machine readiness checks. Video remains unsupported.

This is local stdio tool access for an LLM client, not a public Internet endpoint or universal account integration. The client may share tool results, prompts, saved settings, and local paths with its provider. See [MCP setup and permissions](docs/llm-tools.md). Authenticated cloud-only remote access remains planned.

## Architecture

```text
WinUI 3 views → MVVM view models → Core contracts / Infrastructure
                                      │
                         ┌────────────┴─────────────┐
                         │                          │
                   Rust DLL / C ABI          Python child workers
                   plans · path policy       Hugging Face metadata/setup
                   export · storage inventory isolated Diffusers/CUDA child
                   pinned file verification   model header identification
                   model import copy/hash
```

The Rust ABI uses caller-owned UTF-8 buffers; allocation ownership never crosses language boundaries. Workers use versioned JSON lines over owned child-process streams. Native executable adapters can be added alongside Python workers.

Read [current status](docs/current-status.md), [inference lifecycle](docs/inference.md), [native ABI](docs/native-abi.md), [development](docs/development.md), and the [original architecture proposal](docs/architecture/README.md).

## Build from source

Prerequisites: Windows x64, PowerShell 7, .NET SDK 10, Rust/rustup with the MSVC toolchain, and the Visual C++ linker/Windows SDK required by Rust. Visual Studio Build Tools can supply the native build prerequisites.

```powershell
./scripts/build.ps1
./scripts/check.ps1
./scripts/run.ps1 -NoBuild
```

Source builds also place the MCP console host beside the GUI, so Settings can generate usable client configuration for the development build. The .NET SDK remains a development prerequisite; published installers bundle their runtime.

The Rust version is pinned in `rust-toolchain.toml`, dependencies in `native/Cargo.lock`, and downloaded runtime/compiler artifacts in `packaging/dependencies.json`. Initial builds fetch the official Python embeddable runtime and verify its SHA-256. The application never depends on a developer's global Python installation.

Official releases are built and published by GitHub Actions when the version changes on `main` (see [development](docs/development.md#release-discipline)). Builds refresh the current-release summary above; checks require it to match the version and changelog before publication. Each release has generated installation/feature/license guidance in its release notes and a downloadable README alongside its installer and checksum. To create the self-contained application and installer locally:

```powershell
./scripts/build.ps1 -Configuration Release -Installer
```

Installer builds also download a pinned, checksum-verified Inno Setup compiler into the ignored `.tools` directory. Its upstream licensing terms apply; see [third-party notes](THIRD-PARTY-NOTES.md). Generated artifacts, models, runtimes, logs, and settings are ignored by Git.

Optional network smoke check:

```powershell
./scripts/check.ps1 -Online
```

## License

Media Workbench is released under the [MIT License](LICENSE). Third-party components and models retain their own licenses (Stable Diffusion 1.5 uses CreativeML Open RAIL-M); notices are preserved in `licenses/`, the Windows App SDK notice, and the bundled Python distribution.
