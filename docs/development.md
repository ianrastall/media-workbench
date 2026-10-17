# Development

## Layout

- `src/MediaWorkbench.App`: WinUI views, view models, Windows identity and composition.
- `src/MediaWorkbench.Core`: typed contracts and domain state.
- `src/MediaWorkbench.Infrastructure`: native interop, Python supervisor, settings and application services.
- `src/MediaWorkbench.Cli`: direct worker access and developer diagnostics.
- `native`: Rust core, Cargo lock, and C header.
- `workers/python`: metadata/diagnostic worker and Hub adapter.
- `recipes`, `schemas`: versioned integration data and protocol design.
- `packaging`: pinned runtime/tool sources and installer definition.
- `tests`: real native/worker boundary checks and Python behavioral tests.

## Build and run

Run `scripts/build.ps1` before `scripts/run.ps1 -NoBuild`. The build prepares a checksum-verified private Python runtime, compiles the Rust DLL, and builds the solution. Artifacts are under `artifacts/`. The `runtime/`, `.cache/`, `.tools/`, `artifacts/`, and `dist/` trees are local and ignored.

Use `scripts/build.ps1 -Configuration Release -Installer` to publish and build the per-user installer. Output is `dist/MediaWorkbench-0.1.0-alpha.1-win-x64-setup.exe`, with an adjacent SHA-256 file. The installer defaults to the user's local Programs folder. It creates a Start menu shortcut with AppUserModelID `ianrastall.MediaWorkbench`, matching the running application. Taskbar pinning remains a normal user action.

`scripts/test-publish.ps1` checks the shipped resource files, exercises the bundled worker/native core, and starts the actual GUI briefly to check XAML/binding initialization. Pass `-ApplicationDirectory <installed-folder>` to verify an installed copy. CI uses `-SkipUI` for its hosted runner while still requiring the compiled resource files and runtime check.

The application's configured data folder and settings are outside its install directory and survive uninstall. This release does not download model weights or provision an inference runtime. Changing the data folder does not migrate existing content.

## Workers and native code

The default GUI worker executable is `runtime/python/python.exe` under the app directory. A developer may override it for one launch with `MEDIA_WORKBENCH_PYTHON`; no global environment changes are needed. Keep account secrets out of arguments and logs. The initial worker uses no token and no third-party Python packages.

Native API declarations are in `native/include/media_workbench.h`. Build with `cargo build --manifest-path native/Cargo.toml --target-dir artifacts/native --release --locked`; the .NET projects copy that library into executable outputs. The native Windows MSVC CRT is linked statically through the repository's Cargo configuration.

Run `scripts/check.ps1` for Rust formatting/tests/lints, Python tests, and C# boundary tests. `-Online` adds a small public-network search/detail check; routine unit tests do not need the Hub. `-Ffprobe <absolute-path>` enables the optional native-tool probe. Do not make tests depend on model downloads or a particular GPU.

## Release discipline

Update the version, current status, and release notes together. Build and verify the published/installed executable, not just the source projects. Verify the actual GUI after XAML changes; an earlier resource-scope bug demonstrated why a headless worker check is insufficient. Retain exact runtime hashes and provenance. Review the public Git tree before push; no model caches, credentials, reports with personal paths, or binaries belong in source control.

The installer is currently unsigned and Windows 10 clean-machine validation remains pending. Signing and automated update distribution are separate release milestones.
