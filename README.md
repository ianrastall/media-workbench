# Media Workbench

A dark Windows desktop workspace for local image and video tools, built with **WinUI 3, MVVM, C# 14, .NET 10, a Rust core exposed through a C ABI, and out-of-process Python workers**.

**Status: 0.2.0-alpha.3 — local image creation from text or a picture.** Discover public Hugging Face models, install an isolated Stable Diffusion 1.5 environment, verify your GPU, and generate images from prompts. Video generation remains planned.

## Install

Download the Windows x64 installer from [Releases](https://github.com/ianrastall/media-workbench/releases). The installer adds Media Workbench to your Start menu and installed applications. The app has a stable Windows application identity and can be pinned to the taskbar normally. It does not change your existing taskbar pins.

Target: **Windows 10 22H2 or Windows 11, x64**. This alpha has been tested on Windows 11; Windows 10 clean-machine verification is still pending. The installer is currently unsigned.

The GUI, .NET/Windows App SDK runtime, Rust library, and private discovery Python runtime are bundled. You do not need system Python or .NET. Internet access is required for discovery and first-time model setup. Inference setup downloads its own Python 3.12, CUDA PyTorch, and locked packages into your chosen data folder.

For image generation, this first profile requires an **NVIDIA GPU with at least 6 GB VRAM**, a compatible driver, and **20 GiB free space** during first-time setup (re-verifying an existing installation needs space only for missing files). It produces 512 × 512, 512 × 768, or 768 × 512 images using FP32 computation and CPU offload, with a choice of guidance and three samplers. Other GPU vendors and CPU-only generation are not yet supported.

1. Open **Settings**, choose a data folder on a drive with enough space, and save.
2. Open **Create**, review the linked model card/license, and select **Install / verify**.
3. Setup downloads roughly 5 GiB plus dependencies and tests real inference. A model becomes ready only after the checks pass on your machine.
4. Describe the picture, pick a shape, and select **Generate image**. To start from a picture of your own instead, choose or drag it onto Create (or select **Use as starting picture** on a Library image) and say how much to change it (seed, steps, prompt strength, and sampling method are under **Advanced options**). Your PNG and its settings appear in **Library** and remain in your data folder. Generations that fail, are cancelled, or are stopped by closing the app are listed under **Unfinished generations** with the reason; **Use settings** loads them back into Create and **Open log** shows the inference log.

**Tools** makes any picture 2× or 4× bigger and sharper, offline. Choose or drag a picture there, or select **Make it bigger** on a Library image.

**Updates:** when you're online, Media Workbench checks for a new version at startup and shows what changed on **Home**. Select **Update now** to download it, verify it against its published checksum, and reopen the updated app; nothing installs until you click. After setup, generating images needs no internet.

**Settings → Storage** shows what the data folder uses. You can clear the download caches (about 4 GiB that only speed up repairs) and logs, or remove Stable Diffusion 1.5 entirely; your images and their records are always kept.

Cancel stops the owned worker operation. Retrying setup reuses completed downloads and the Hub cache; an incomplete bootstrap archive restarts. A data folder typed in Settings takes effect only after **Save settings**. Changing data folders does not move existing models or images.

## What works now

- Native dark WinUI shell with MVVM commands and observable state.
- Live public Hugging Face search with task filters.
- Model details: publisher/repository, revision, task, library, license metadata, access state, and repository files.
- Rust installation-plan validation, relative-path policy, and streaming SHA-256 through C ABI 1.
- Bundled Python worker diagnostics and read-only NVIDIA GPU detection.
- Persistent storage settings, cancellation, and session activity.
- Pinned SD 1.5 model files with SHA-256 checks and an exact, hash-locked inference dependency set.
- Private runtime provisioning, persistent installation attempts, progress, retry, and a real GPU verification gate.
- Worker process trees owned by a Windows Job Object: closing or crashing the app stops setup and inference children, and unfinished generations are recorded as interrupted.
- Prompt/negative prompt, deterministic seed, 1–50 inference steps, local PNG output, preview, and saved artifact history.
- Per-user install/uninstall, Start menu shortcut, custom icon, taskbar identity, and single-instance behavior.

Discovery results do not imply installation support: **only the bundled SD 1.5 recipe is currently executable**. Protected Hugging Face access, arbitrary model installation, enhancement tools, image editing, and video pipelines remain in development. Model licensing is separate from the application license.

## Architecture

```text
WinUI 3 views → MVVM view models → Core contracts / Infrastructure
                                      │
                         ┌────────────┴─────────────┐
                         │                          │
                   Rust DLL / C ABI          Python child workers
                   plans · path policy       Hugging Face metadata/setup
                   file verification         isolated Diffusers/CUDA child
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

The Rust version is pinned in `rust-toolchain.toml`, dependencies in `native/Cargo.lock`, and downloaded runtime/compiler artifacts in `packaging/dependencies.json`. Initial builds fetch the official Python embeddable runtime and verify its SHA-256. The application never depends on a developer's global Python installation.

Official releases are built and published by GitHub Actions when the version changes on `main` (see [development](docs/development.md#release-discipline)). To create the self-contained application and installer locally:

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
