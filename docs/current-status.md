# Current implementation status

Version: **0.2.0-alpha.2**. Development started 17 October 2026.

## Technology decisions

| Layer | Selected implementation |
|---|---|
| Windows UI | WinUI 3, dark theme, custom title bar, DPI-aware initial sizing |
| Presentation | CommunityToolkit.Mvvm, observable partial properties, commands |
| Managed language/runtime | C# 14, .NET 10 |
| Native internals | Rust 2024 edition, pinned Rust 1.97.0, C ABI 1 |
| Worker protocol | UTF-8 JSON lines over local child stdin/stdout; stderr diagnostics |
| Public Hub discovery | Standard-library Python HTTPS client |
| Bundled discovery runtime | Official CPython 3.14.8 x64 embeddable distribution |
| Installer | Per-user Inno Setup, self-contained app bundle |

The small metadata/setup worker has no machine-learning packages. An independently provisioned Python 3.12 child loads a locked PyTorch CUDA 12.6 / Diffusers profile. No AI runtime enters the GUI process.

## Implemented

Live public model search and details, native planning and SHA-256, worker health, storage settings, visible errors/cancellation, session activity, application identity, singleton launch, Start menu integration, and installer build/publish scripts. The GUI uses real view-model commands rather than implementing operations in click handlers. View code handles navigation, title bar, and window geometry.

Create provisions a private Python/uv runtime and hash-locked wheels, fetches selected SD 1.5 safetensors/configuration files at an immutable Hub commit, verifies all model SHA-256 values, and tests actual 512 × 512 CUDA inference before recording ready state. It supports a prompt, negative prompt, seed, 1–50 steps, progress/cancel, PNG preview, and durable settings/output evidence. Library shows up to 200 recent successful images in a virtualized grid with thumbnail-size decoding, and up to 50 recent failed, cancelled, or interrupted generations with their reason, a button to reuse their settings in Create, and their own inference log when one exists. Settings edits a draft folder path that takes effect only when saved; every operation uses the saved folder. Setup attempts survive restarts and can be retried with retained downloads. Each worker process tree is owned by a kill-on-close Windows Job Object, so an app crash or forced stop cannot leave the inference child running, and generation jobs orphaned by a crash are recorded as interrupted. See [inference lifecycle](inference.md).

## Still planned

Protected Hugging Face account access, arbitrary model installation, additional hardware/runtime profiles, image editing, video generation, an indexed artifact database, queues, enhancement/tool installation, cache pruning, and application updates. Tools displays its development status. The SD 1.5 recipe remains experimental and each installation must pass its own inference test; discovery results do not imply runnable pipelines.

## Verification

- Rust unit tests, formatting, and Clippy are part of `scripts/check.ps1`.
- Python unit tests cover request validation, cancellation, search normalization, input rejection, and repository metadata.
- Python tests also cover recovery of orphaned generation jobs, preserving jobs whose owner is alive, and skipping damaged records.
- Python tests validate bundled recipes and live worker messages against `schemas/`, run the coordinator's native installation gate through the real Rust library, size re-verification free space, and check tracked files for personal paths, private email addresses, and built binaries.
- Python tests cover the readiness stamp (adapter version and pinned files, not worker source or line endings), size/mtime checks that replace per-generation hashing, the recipe-driven VRAM limit and GPU selection, unreadable nvidia-smi output, and Library path containment for relative, traversal, and moved-folder records.
- C# checks load the UI-free view model with a recording fake service (saved versus draft data folder, unfinished-job listing and retry) and exercise a real Python child, native C ABI loading, hashing, path policy, installation blocking, settings round trips, the view model's saved-versus-draft data folder, cancellation, protocol failures, unexpected process exit, and termination of a descendant left behind by a failed worker.
- The optional live-network check searches public Hub models and reads a returned repository.
- `MediaWorkbench.exe --self-test <absolute-report-path>` checks the published/installed native core and bundled worker without using system Python.
- `MediaWorkbench.exe --ui-self-test <absolute-report-path>` checks the real window/resource/binding initialization; `scripts/test-publish.ps1` combines both checks.
- Actual UI launch, navigation, public search, and model selection are checked interactively during development.

Windows 11 is the current test host. The installer targets Windows 10 22H2 and Windows 11 x64; a clean Windows 10 test is pending. The first inference profile has passed real 512 × 512 generation on a GTX 1660 Ti (6 GB); this does not certify other GPUs or drivers. See [validation](validation.md) and release notes for the tested build.

## Next areas

1. Clean-machine Windows 10/11 and additional GPU/driver coverage.
2. Storage inspection/pruning and richer durable job diagnostics, including showing interrupted/failed jobs.
3. A warm inference worker, queue, and indexed/virtualized artifact library.
4. More model adapters, image editing, and video only after explicit runtime/hardware validation.

This deliberately follows a complete working path before expanding model coverage or video features.
