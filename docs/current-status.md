# Current implementation status

Version: **0.1.0-alpha.1**. Development started 17 October 2026.

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

The small metadata worker deliberately has no machine-learning packages. A later inference worker will use an independently tested Python/PyTorch/Diffusers profile; the discovery interpreter version is not a claim about inference compatibility.

## Implemented

Live public model search and details, native planning and SHA-256, worker health, storage settings, visible errors/cancellation, session activity, application identity, singleton launch, Start menu integration, and installer build/publish scripts. The GUI uses real view-model commands rather than implementing operations in click handlers. View code handles navigation, title bar, and window geometry.

## Still planned

Protected Hugging Face account access, verified model recipes, automatic inference-environment installation, model downloads, image generation, image editing, video generation, durable job state/database, artifact library, tool installation, packaging updates, and production-grade process ownership/recovery. Create, Library, and Tools display their development status.

## Verification

- Rust unit tests, formatting, and Clippy are part of `scripts/check.ps1`.
- Python unit tests cover request validation, cancellation, search normalization, input rejection, and repository metadata.
- C# checks exercise a real Python child, native C ABI loading, hashing, path policy, installation blocking, settings round trips, cancellation, protocol failures, and unexpected process exit.
- The optional live-network check searches public Hub models and reads a returned repository.
- `MediaWorkbench.exe --self-test <absolute-report-path>` checks the published/installed native core and bundled worker without using system Python.
- `MediaWorkbench.exe --ui-self-test <absolute-report-path>` checks the real window/resource/binding initialization; `scripts/test-publish.ps1` combines both checks.
- Actual UI launch, navigation, public search, and model selection are checked interactively during development.

Windows 11 is the current test host. The installer targets Windows 10 22H2 and Windows 11 x64; a clean Windows 10 test is pending. No inference compatibility or speed claims have been made. See release notes for the final validation of a particular build.

## Next vertical slice

1. Persist a model installation attempt and its resolved recipe.
2. Resolve a pinned model revision and exact inference dependency lock.
3. Provision that runtime in the selected data folder, with visible download/install progress.
4. Fetch the selected model files through Hugging Face's supported SDK/cache.
5. Verify real inference on the target GPU, then mark the installation ready.
6. Add prompt-to-image creation, validated artifact finalization, and history.

This deliberately follows a complete working path before expanding model coverage or video features.
