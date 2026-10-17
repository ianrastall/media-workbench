# Media Workbench

A dark Windows desktop workspace for local image and video tools, built with **WinUI 3, MVVM, C# 14, .NET 10, a Rust core exposed through a C ABI, and out-of-process Python workers**.

**Status: 0.1.0-alpha.1 — development has begun.** This alpha provides live model discovery and diagnostics. Automatic model installation and image/video generation are upcoming milestones, not implemented features.

## Install

Download the Windows x64 installer from [Releases](https://github.com/ianrastall/media-workbench/releases). The installer adds Media Workbench to your Start menu and installed applications. The app has a stable Windows application identity and can be pinned to the taskbar normally. It does not change your existing taskbar pins.

Target: **Windows 10 22H2 or Windows 11, x64**. This alpha has been tested on Windows 11; Windows 10 clean-machine verification is still pending. The installer is currently unsigned.

The GUI, .NET/Windows App SDK runtime, Rust library, and private discovery Python runtime are bundled. You do not need to install .NET, Python, or a Python package to browse models. Internet access is required for live Hugging Face discovery. Future inference environments will be installed separately through verified recipes.

## What works now

- Native dark WinUI shell with MVVM commands and observable state.
- Live public Hugging Face search with task filters.
- Model details: publisher/repository, revision, task, library, license metadata, access state, and repository files.
- Rust installation-plan validation, relative-path policy, and streaming SHA-256 through C ABI 1.
- Bundled Python worker diagnostics and read-only NVIDIA GPU detection.
- Persistent storage settings, cancellation, and session activity.
- Per-user install/uninstall, Start menu shortcut, custom icon, taskbar identity, and single-instance behavior.

Discovery does not establish that a model is supported by Media Workbench or will run on your hardware. No model weights are downloaded by this alpha. Account access, durable generation history, enhancement tools, video pipelines, and managed inference setup remain in development.

## Architecture

```text
WinUI 3 views → MVVM view models → Core contracts / Infrastructure
                                      │
                         ┌────────────┴─────────────┐
                         │                          │
                   Rust DLL / C ABI          Python child workers
                   plans · path policy       Hugging Face metadata
                   file verification         future Diffusers engines
```

The Rust ABI uses caller-owned UTF-8 buffers; allocation ownership never crosses language boundaries. Workers use versioned JSON lines over owned child-process streams. Native executable adapters can be added alongside Python workers.

Read [current status](docs/current-status.md), [native ABI](docs/native-abi.md), [development](docs/development.md), and the [original architecture proposal](docs/architecture/README.md).

## Build from source

Prerequisites: Windows x64, PowerShell 7, .NET SDK 10, Rust/rustup with the MSVC toolchain, and the Visual C++ linker/Windows SDK required by Rust. Visual Studio Build Tools can supply the native build prerequisites.

```powershell
./scripts/build.ps1
./scripts/check.ps1
./scripts/run.ps1 -NoBuild
```

The Rust version is pinned in `rust-toolchain.toml`, dependencies in `native/Cargo.lock`, and downloaded runtime/compiler artifacts in `packaging/dependencies.json`. Initial builds fetch the official Python embeddable runtime and verify its SHA-256. The application never depends on a developer's global Python installation.

Create the self-contained application and installer:

```powershell
./scripts/build.ps1 -Configuration Release -Installer
```

Installer builds also download a pinned, checksum-verified Inno Setup compiler into the ignored `.tools` directory. Its upstream licensing terms apply; see [third-party notes](THIRD-PARTY-NOTES.md). Generated artifacts, models, runtimes, logs, and settings are ignored by Git.

Optional network smoke check:

```powershell
./scripts/check.ps1 -Online
```

## License

An application license has not yet been selected. Public visibility alone does not grant an open-source license. Third-party components retain their own licenses; notices are preserved in `licenses/`, the Windows App SDK notice, and the bundled Python distribution.
