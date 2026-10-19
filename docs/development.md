# Development

Start with [handoff](../HANDOFF.md) for exact resume commands and [stack alignment](stack-alignment.md) for the current architecture decisions, deliberate exceptions, and remaining acceptance evidence. NuGet versions are centralized in `Directory.Packages.props`; committed project lockfiles are enforced for every CI restore. Update and review the locks deliberately when changing packages. Native executable hosts compile as x64, reject conflicting platforms/RIDs, and load the Rust core from beside the executable.

## Layout

- `src/MediaWorkbench.App`: WinUI views, converters, Windows identity and composition.
- `src/MediaWorkbench.Presentation`: UI-free view models (CommunityToolkit.Mvvm), loaded directly by the C# checks.
- `src/MediaWorkbench.Core`: typed contracts and domain state.
- `src/MediaWorkbench.Infrastructure`: native interop, Python supervisor, settings and application services.
- `src/MediaWorkbench.Cli`: direct worker access and developer diagnostics.
- `native`: Rust core, Cargo lock, and C header.
- `workers/python`: metadata/setup coordinator, Hub adapter, and isolated inference child.
- `recipes`, `profiles`, `schemas`: integration recipes, runtime/model locks, and protocol design.
- `packaging`: pinned runtime/tool sources and installer definition.
- `tests`: real native/worker boundary checks, view-model checks against a recording fake service, and Python behavioral, schema, and repository-hygiene tests.

## Build and run

Run `scripts/build.ps1` before `scripts/run.ps1 -NoBuild`. The build prepares a checksum-verified private Python runtime, compiles the Rust DLL, and builds the solution. Artifacts are under `artifacts/`. The `runtime/`, `.cache/`, `.tools/`, `artifacts/`, and `dist/` trees are local and ignored.

`build.ps1 -ManagedArtifacts <absolute-folder>` places managed build/intermediate outputs in a separate folder, useful for verifying a publish while an existing development app remains open. Publish output still goes to `artifacts/publish/win-x64`; `run.ps1` uses the ordinary default Debug output. Native builds retain the shared Cargo target folder and must not replace a library currently used by a source worker.

Use `scripts/build.ps1 -Configuration Release -Installer` to publish and build the per-user installer. Output is `dist/MediaWorkbench-<version>-win-x64-setup.exe`, with an adjacent SHA-256 file. The installer defaults to the user's local Programs folder. It creates a Start menu shortcut with AppUserModelID `ianrastall.MediaWorkbench`, matching the running application. Taskbar pinning remains a normal user action.

`scripts/test-publish.ps1` checks the shipped resource files, exercises the bundled worker/native core, and starts the actual GUI briefly to check XAML/binding initialization. Pass `-ApplicationDirectory <installed-folder>` to verify an installed copy. CI uses `-SkipUI` for its hosted runner while still requiring the compiled resource files and runtime check.

The configured data folder and settings are outside the install directory. The first inference profile downloads models and a separate private runtime there; these are not installer payloads. Changing the data folder does not migrate existing content.

## Workers and native code

The default GUI worker executable is `runtime/python/python.exe` under the app directory. In Debug builds only, a developer may override it for one launch with `MEDIA_WORKBENCH_PYTHON`; Release builds ignore the variable and always run the bundled interpreter. Keep account secrets out of arguments and logs. The initial worker uses no token and no third-party Python packages.

The ML child is launched with `-I -u`: the Python user site and inherited Python configuration cannot contaminate the profile. Do not remove `-I` when adding worker operations. See [inference lifecycle](inference.md) for package/model pins, download provenance, and readiness gates.

The CLI exposes `install`, `status`, `generate`, `library`, and the read-only `storage` summary for opt-in hardware checks. `update-check [pretend-version] [--download <absolute-data-folder>]` queries GitHub Releases like the app does and, with `--download`, fetches and verifies the installer into that folder's `temp/updates` without running it. Worker commands given a data folder keep their temporary files in its `temp/work`; `health`, `catalog` and `demo` need none. These require an explicit absolute data folder; `install` can download several GiB and performs real GPU work. After building, for example:

```powershell
$taskCli = './artifacts/dotnet/bin/MediaWorkbench.Cli/debug/MediaWorkbench.Cli.exe'
$taskPython = (Resolve-Path './runtime/python/python.exe').Path
$taskApplication = (Get-Location).Path
& $taskCli $taskPython $taskApplication install 'D:\MediaWorkbenchData'
& $taskCli $taskPython $taskApplication generate 'D:\MediaWorkbenchData' 'A watercolor landscape' 20
```

Routine CI never installs this ML profile or downloads weights. Profile updates must rerun real inference/cancellation checks; a changed profile digest invalidates old readiness records.

Native API declarations are in `native/include/media_workbench.h`. Build with `cargo build --manifest-path native/Cargo.toml --target-dir artifacts/native --release --locked`; the .NET projects copy that library into executable outputs. The native Windows MSVC CRT is linked statically through the repository's Cargo configuration.

Storage inventory and verified image export run directly in Rust on a background thread, without launching Python. Worker clients use thin ctypes adapters to those services. The shared `profiles/storage-layout.json` lists the supported runtime/model folders for inventory; its IDs are checked against the inference coordinator in tests. Storage sizes are logical file sizes. Removal remains an exclusive worker operation with its existing readiness invalidation and preservation of images/jobs.

Run `scripts/check.ps1` for Rust formatting/tests/lints, Python tests, and C# boundary tests. `-Online` adds a small public-network search/detail check; routine unit tests do not need the Hub. `-Ffprobe <absolute-path>` enables the optional native-tool probe. Do not make tests depend on model downloads or a particular GPU.

The online check also validates a real pinned single-file header through the Rust Hub planner, downloading only bounded header bytes and requiring no GPU. For a full opt-in Discover download/use/removal check, run the bundled discovery Python with `tests/gpu_model_import_check.py <verified-data-folder> hf:publisher/model --license-reviewed` after reviewing the pinned source's license. It downloads weights and performs real GPU work; `--keep` retains the added model, otherwise removal keeps its generated images/jobs. Routine checks use sparse fixtures. See [Discover downloads](inference.md#discover-downloads).

## MCP host

Normal `build.ps1` builds compile the console host for the GUI's self-contained runtime and copy its launch files beside the GUI, sharing the runtime and Core/Infrastructure dependencies. Settings-generated client configuration therefore works from the development output as well as an installed/published application. Building still requires the .NET SDK; launching the resulting host uses the bundled runtime.

The CLI also supports `MediaWorkbench.Cli.exe mcp --data-root <absolute-folder>` with optional `--allow-setup` and `--allow-inference`. `--root <absolute-application-root>` is available for a source-checkout host; installed clients infer the root beside the executable. All stdout is bounded JSON-RPC MCP traffic; errors go to stderr. Settings generates the local-client configuration. See [LLM tools](llm-tools.md).

Publishing builds the console host self-contained in a separate staging folder, then merges its files into the GUI bundle so both use the same .NET/native/service payload. `test-publish.ps1` validates the shipped console host, default permissions, and actual bundled-worker status call. C# checks cover real stdio sessions, disabled capabilities, folder scoping, validation, cancellation, concurrent-operation refusal, client EOF, and bounded input. Temporary fixture data can be directed to a separate volume with `scripts/check.ps1 -Artifacts <absolute-check-directory>`; generated reports remain local.

## Release discipline

For headless preference-migration acceptance, the shipped console host supports `MediaWorkbench.Cli.exe migrate-preferences <absolute-profile-folder>`. It invokes the same loader/migration as the GUI, reports the chosen folder and preference/location paths as JSON, and performs no model downloads or inference. The profile folder must be explicit, and the app must be closed; the command shares its desktop mutex. Published smoke checks verify that guard, then use disposable legacy preferences, verify migration and the small pointer, and remove their fixture. A missing or invalid destination exits with an error and never falls back to legacy settings.

Update the version, current status, changelog, and README together. `Directory.Build.props` is the only version source; `scripts/get-version.ps1` reads it for the installer. `build.ps1` automatically refreshes the managed current-release section in the main README from that version and its dated changelog section; `scripts/update-release-readme.ps1` can refresh it without building. Commit the result with the release. Hand-authored requirements, capabilities, and limitations elsewhere in README must describe the actual code. Checks and CI reject stale summaries, missing/duplicate/empty release sections, and missing installation guidance.

Releases are built and published by GitHub Actions: pushing a new version with its matching `CHANGELOG.md` section to `main` makes the `release` job in `.github/workflows/build.yml` wait for the checks job, then publish the installer and checksum that job built and checked as `v<version>` (pre-release when the version contains `-`). `scripts/release-notes.ps1` combines the changelog with the current README's installation, feature, MCP, and license sections, adds exact download/checksum instructions, and pins documentation links to the version tag. The generated Markdown is both the release body and a `MediaWorkbench-<version>-README.md` asset. A push without a version change does not create a release. Generation checks exercise stale block replacement, idempotence, section validation, and links without contacting GitHub.

Build and verify the published/installed executable, not just the source projects. Verify the actual GUI after XAML changes; an earlier resource-scope bug demonstrated why a headless worker check is insufficient. Retain exact runtime hashes and provenance. Review the public Git tree before push; no model caches, credentials, reports with personal paths, or binaries belong in source control. `tests/test_repository_hygiene.py` fails the checks if a tracked file contains a user-profile path, this machine's paths or name, a non-public email address, or a built binary. Commit with your GitHub no-reply address (`git config user.email <id>+<login>@users.noreply.github.com`) so commit metadata stays public-safe.

The installer is currently unsigned and Windows 10 clean-machine validation remains pending. Signing and clean-machine update installation validation remain separate release milestones; checksum-verified update distribution is implemented.
