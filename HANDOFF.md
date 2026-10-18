# Media Workbench handoff

Updated 18 October 2026 for **0.2.0-alpha.16**. Repository: <https://github.com/ianrastall/media-workbench>, branch `main`. Run `git rev-parse HEAD` and `git status --short` to identify the exact checkout and any later unfinished work; [validation](docs/validation.md) distinguishes observed results from planned checks.

## Where development stopped and resumed

The historical starting point was `b0b0a2b` (alpha.12). The working tree already contained alpha.13 hardware-aware inference, UI/contracts, tests, a version bump, and draft documentation, plus untracked hardware modules. Its generated README release summary was still alpha.12 and alpha.13 had no validation entry.

The resumed work completes that slice, removes duplicate Python drive detection in favor of Rust, fixes Diagnostics readiness for missing/changed runtime/model files and incompatible cards, and tightens stack/build requirements. Central NuGet versions and committed locks retain the existing package versions. CI requires locked restore; native executable hosts are explicitly x64, conflicting settings are rejected, native loading is app-local, and published checks inspect executable architectures. See [stack alignment](docs/stack-alignment.md) for exceptions and outstanding acceptance work.

Alpha.14 adds Discover's public single-file SD 1.5 download path: native metadata planning, bounded remote header identification, computer/space/runtime checks, pinned download with retained notices, and the existing real GPU readiness gate. Snapshot changes invalidate review; generation retains the pinned Hub provenance. The baseline before this slice was `6201fa4`. No adapter/runtime profile change is needed, and existing alpha.13 installations stay verified. See [inference](docs/inference.md#discover-downloads) for implementation and limits.

Alpha.15 resumes from `ec1e5f5` and moves SD 1.5 tensor-layout compatibility to Rust for local checkpoints, Diffusers parts, pinned Discover headers and copied-model checks. Python retains bounded header/offset parsing and folder configuration handling. Copied Diffusers files repeat weight-format checks before GPU testing. Inference settings, adapter versions and readiness stamps do not change. This milestone uses headless checks and no GPU work; interactive validation remains pending as already recorded for alpha.14.

Alpha.16 resumes from `72a7a2e` and separates model loading from inference: the ML child records `startupSeconds`, `loadSeconds` and `seconds` for generation, verification and upscaling, and the coordinator stores a plain `loadNote` (from `hardware.load_note` and Rust `mw_drive_info`) when a generation's load took 20 s or more on a hard disk, USB or network folder, or ran below 100 MiB/s on another drive. Create shows the load time and note after saving; Library/MCP return them. No inference, adapter or readiness change; headless checks only. Calibration against a real hard-disk folder and collecting cold/warm timings for the warm-worker decision are the next measurements.

## Build, check, run

The user relies on this computer during development. Prefer headless work; announce necessary desktop control and explain any apparently idle window before using it. A user interruption stops automation and is not failure evidence. Keep each milestone tested, documented, committed and released through Actions; keep this handoff current when switching assistants or approaching the end of a session. Session-usage counters are not available to the development agent.

From the repository root in PowerShell:

```powershell
./scripts/build.ps1
./scripts/check.ps1
./scripts/build.ps1 -Configuration Release -Publish
./scripts/test-publish.ps1 -SkipUI
./scripts/run.ps1 -NoBuild
```

Build prerequisites: Windows x64, the SDK selected by `global.json` (10.0.113, patch-only roll-forward), Rustup with `rust-toolchain.toml` (1.97.0, rustfmt and Clippy), and Visual Studio 2022 Build Tools with the MSVC x64 toolchain and Windows SDK. The project targets Windows SDK 26100; the NuGet BuildTools version is pinned separately. Cargo runs from the repository root so `.cargo/config.toml` selects static MSVC CRT linkage. The x64 build maps to `x86_64-pc-windows-msvc`; other architectures are rejected. Design-time builds do not invoke Cargo.

`build.ps1` prepares checksum-verified private tooling, builds Rust and the solution, and copies a console host beside the GUI for development MCP use. It may download missing dependencies. `check.ps1` runs Rust formatting/tests/Clippy, Python tests, and C# boundary/MCP/presentation checks; it does not provision ML models. `-Artifacts <absolute-folder>` redirects disposable C# check data when the project drive is busy. `-Online` adds public Hub discovery validation.

`-Publish` produces a self-contained Release folder and runs headless published checks. `test-publish.ps1` adds actual window/XAML initialization on an interactive desktop. The published folder is `artifacts/publish/win-x64`; normal Debug GUI output is `artifacts/dotnet/bin/MediaWorkbench.App/debug_win-x64`. Installer production and release publication belong to GitHub Actions. Push a changed `<Version>` and matching dated changelog section together; never manually create the release from this computer.

Routine restore updates locks only when inputs change. To deliberately change a dependency, update `Directory.Packages.props`, run `dotnet restore MediaWorkbench.slnx --force-evaluate --artifacts-path artifacts/dotnet`, review all `packages.lock.json` changes, then run checks/publish. CI sets `ContinuousIntegrationBuild=true`, which enables NuGet locked mode for every restore, including implicit build/publish restores. `dotnet restore MediaWorkbench.slnx --locked-mode --artifacts-path artifacts/dotnet` reproduces the graph check locally.

## Ownership, locations, and shutdown

- ABI/header: `native/include/media_workbench.h`; contract: [native ABI](docs/native-abi.md). Rust owns planning, tensor-layout compatibility, path policy, verified export, storage inventory, pinned-file verification, import copying, drive information and automatic data-folder clean-up.
- C# Core/Presentation/Infrastructure own typed projections, UI state, OS services, native bindings, and worker/process lifetime. WinUI code handles views, navigation, pickers, activation, and painting input. Native buffers are copied and callbacks are retained only until a call returns.
- Standard-library Python coordinates Hub/setup, job records, model readiness and the Library index. ML packages run only in a separate isolated Python child. Windows Job Objects terminate the entire owned tree on cancellation/crash. New queue/warm-worker work must preserve this protocol.
- The saved absolute data folder is selected in Settings. Its models, runtimes, artifacts, private input copies, jobs, caches, index and logs are outside the app installation. Settings changes do not migrate data. `%LOCALAPPDATA%/MediaWorkbench/settings.json` holds local preferences; `%TEMP%` holds disposable hand-overs/update staging.
- `runtime/`, `artifacts/`, `.cache/`, `.tools/`, `.local/` and `dist/` are ignored. Never commit models, machine settings, binaries, generated images, private paths or credentials. Preserve MIT and third-party/model notices.

## Immediate next work

Headless validation can build into a separate managed-output folder with `./scripts/build.ps1 -Configuration Release -Publish -ManagedArtifacts (Join-Path (Get-Location) 'artifacts/dotnet-validation')` when the user's existing app holds its normal output files open. Do not close or replace a running app to make a build succeed. `test-publish.ps1` without `-SkipUI` opens a window; use it only for an announced interactive check when the user is available.

1. Obtain real RTX/Pascal execution evidence for faster settings with `tests/gpu_hardware_profile_check.py <absolute-data-folder> --expect <runtime>` using the bundled discovery Python after building. It runs Install / verify, generation and 4x upscaling; use a folder with sufficient disk space and allow GPU work. Record actual evidence, not a simulated profile result.
2. Run the clean-machine Windows 10/11 installer and alpha.12 upgrade/offline/update/uninstall matrix. Complete accessibility, external startup/idle measurements, useful release-symbol archival, and mixed debugging. Signing remains a distribution decision.
3. Load and inference times are now recorded per job (alpha.16). Collect cold and repeated generations with the GPU check scripts (they print load time), then build the durable queue and warm-child lifecycle. Discover compatibility/pinned installation is implemented for public SD 1.5 single-file checkpoints. Direct folder downloads, protected access, other architectures and video remain unsupported until their dedicated verification paths exist.
4. Follow the queue/warm-child acceptance criteria in [next steps](docs/next-steps.md), then expand models/video with dedicated verification.

Local generation may reduce reliance on datacenter services, but this project has not measured net water, electricity, hardware-demand or cost savings. Do not turn that motivation into a verified environmental claim.
