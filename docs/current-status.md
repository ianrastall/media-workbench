# Current implementation status

Version: **0.2.0-alpha.8**. Development started 17 October 2026.

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

Create provisions a private Python/uv runtime and hash-locked wheels, fetches selected SD 1.5 safetensors/configuration files at an immutable Hub commit, verifies all model SHA-256 values, and tests actual 512 × 512 CUDA inference before recording ready state. It supports a prompt, negative prompt, seed, 1–50 steps, guidance 1–20, square/portrait/landscape sizes (512 × 512, 512 × 768, 768 × 512), a sampler (the model's PNDM default, DPM++ 2M Karras, or Euler ancestral), image-to-image from a chosen, dropped, or Library picture with three change levels, progress/cancel, PNG preview, and durable settings/output evidence. Library searches prompts, negative prompts, seeds, and job IDs, filters generated/upscaled images, and pages through all matching successful images (48 per page) in a virtualized grid with thumbnail-size decoding. A rebuildable SQLite index reads changed JSON records on refresh; image paths and existence are rechecked. Use settings restores a successful image in Create or Tools, including its private starting picture. Open image uses the default PNG viewer; Save a copy calls Rust directly for job validation, filesystem containment, cancellable streaming/hash verification, and atomic export, via the native Windows App SDK picker, verifying the recorded checksum before atomically replacing the destination outside the managed data folder. Both actions are available on the latest Create/Tools result too. Library also shows up to 50 recent failed, cancelled, or interrupted generations with their reason, a button to reuse their settings in Create, and their own inference log when one exists. Settings edits a draft folder path that takes effect only when saved; every operation uses the saved folder. Tools upscales a chosen, dropped, or Library picture 2× or 4× with Real-ESRGAN x4plus (weights pinned by SHA-256, verified on the GPU during Install / verify), saving the result to Library. Home checks GitHub Releases (at startup when online, unless turned off, and on request), shows the newer version's notes, and on **Update now** downloads and SHA-256-verifies the installer, releases the single-instance mutex, runs it silently, and exits; the installer reopens the app. Runtime and model components follow the app: a version that changes the pinned profile asks for Install / verify, which downloads only what changed. Its Storage section measures each part of the data folder and, after confirmation, clears download caches or logs or removes the SD 1.5 runtime, model, and caches while keeping images and job records. Setup attempts survive restarts and can be retried with retained downloads. Each worker process tree is owned by a kill-on-close Windows Job Object, so an app crash or forced stop cannot leave the inference child running, and generation jobs orphaned by a crash are recorded as interrupted. See [inference lifecycle](inference.md).

The installer also includes a self-contained local MCP stdio console host. Settings generates configuration for a compatible LLM client, scoped to the saved data folder. It defaults to inspection; launch-time flags separately permit bundled-model setup and verified generation/upscaling. The client owns its provider account; the model cannot change the folder or enable permissions. It returns image paths/settings rather than image bytes. The host uses the same C# worker supervisor and kill-on-close Job Objects. [MCP setup](llm-tools.md) describes the supported tools, privacy, and transport limits.

Release builds generate the main README's version/date, release links, and changelog summary before committing; checks and the release job reject stale summaries. GitHub Actions publishes the checked installer, its SHA-256, and a generated release README. The release body includes current installation requirements, features, MCP guidance, licensing, and checksum steps from the public README with version-pinned documentation links. The public repository also has relevant GitHub topics and a descriptive summary.

Rust now owns storage inventory as well as export: cancellable traversal, logical bytes/file counts, installation totals, and Windows available disk space. Desktop, MCP, and CLI inspection call it directly without Python; worker clients use a thin adapter. The supported profile/model IDs come from the bundled storage layout. Links/reparse points are rejected in managed area ancestors and skipped inside trees; access failures do not produce partial totals. Removal retains the existing confirmed, locked worker implementation and readiness invalidation.

## Still planned


Authenticated remote/cloud MCP access, protected Hugging Face account access, arbitrary model installation, additional hardware/runtime profiles, masked image editing, video generation, queues, more enhancement tools, automatic cache pruning, and signed application updates. Tools supports Real-ESRGAN upscaling; Home supports checksum-verified application updates. The SD 1.5 recipe remains experimental and each installation must pass its own inference test; discovery results do not imply runnable pipelines.

## Verification

- Rust unit tests, formatting, and Clippy are part of `scripts/check.ps1`.
- Storage checks cover real native C#/Python calls, operation without a Python executable, profile-specific counts, Unicode roots, cancellation before/during traversal, buffer queries without folder creation, Windows junction containment, and the published app/MCP payload.
- Python unit tests cover request validation, cancellation, search normalization, input rejection, and repository metadata.
- Python tests also cover recovery of orphaned generation jobs, preserving jobs whose owner is alive, and skipping damaged records.
- Export tests cover byte-identical copies, changed or missing originals, malformed records, moved folders, cancellation, failed replacement, managed-folder protection, and existing-destination preservation. C# checks cover routing through the saved data folder and the real native ABI boundary.
- Library tests cover paging past 200 images with deterministic ties, Unicode/literal search and image-type filters, incremental JSON reads, moved folders, file removal/restoration, index deletion/corruption/version rebuild, invalid record types, and cancellation rollback. C# checks exercise paging and settings reuse in the view model and search through a real Python/SQLite child. Published-app self-test also opens the bundled SQLite Library.
- Python tests validate bundled recipes and live worker messages against `schemas/`, run the coordinator's native installation gate through the real Rust library, size re-verification free space, and check tracked files for personal paths, private email addresses, and built binaries.
- Python tests cover the readiness stamp (adapter version and pinned files, not worker source or line endings), size/mtime checks that replace per-generation hashing, the recipe-driven VRAM limit and GPU selection, unreadable nvidia-smi output, and Library path containment for relative, traversal, and moved-folder records.
- C# checks load the UI-free view model with a recording fake service (saved versus draft data folder, unfinished-job listing and retry) and exercise a real Python child, native C ABI loading, hashing, path policy, installation blocking, settings round trips, the view model's saved-versus-draft data folder, cancellation, protocol failures, unexpected process exit, and termination of a descendant left behind by a failed worker.
- MCP checks exercise real stdio initialization/tool calls, launch capabilities, folder scoping, typed arguments, malformed and oversized requests, active-operation exclusion, cancellation, and client disconnect. Published-payload tests run the shipped console host and bundled worker.
- The optional live-network check searches public Hub models and reads a returned repository.
- `MediaWorkbench.exe --self-test <absolute-report-path>` checks the published/installed native core and bundled worker without using system Python.
- `MediaWorkbench.exe --ui-self-test <absolute-report-path>` checks the real window/resource/binding initialization; `scripts/test-publish.ps1` combines both checks.
- Actual UI launch, navigation, public search, and model selection are checked interactively during development.

Windows 11 is the current test host. The installer targets Windows 10 22H2 and Windows 11 x64; a clean Windows 10 test is pending. The first inference profile has passed real 512 × 512 generation on a GTX 1660 Ti (6 GB); this does not certify other GPUs or drivers. See [validation](validation.md) and release notes for the tested build.

## Next areas

1. Clean-machine Windows 10/11 and additional GPU/driver coverage.
2. A warm inference worker and durable generation queue, retaining owned-process cancellation and crash recovery.
3. Library organization, richer metadata export, and selective storage pruning; browsing is now indexed, paged, and virtualized.
4. More model adapters, image editing, and video only after explicit runtime/hardware validation.

This deliberately follows a complete working path before expanding model coverage or video features.

See [development direction](next-steps.md) for the queue, warm-worker sequencing, and acceptance criteria. These remain planned.
