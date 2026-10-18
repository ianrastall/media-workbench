# Next milestone: choose where Media Workbench works

Recorded 18 October 2026. **Planned; not implemented or released.** Development is paused at 0.3.0-alpha.1. Drive containment is the next release milestone, ahead of queue/warm-worker work and further model expansion. The intended release is **0.4.0-alpha.1**, subject to the acceptance checks below; do not bump the shipped version or add a release changelog entry until it works.

## Problem and current evidence

Choosing a data folder does not currently choose every write location. An SSD can be unsuitable because it is small, nearly full, reserved for Windows, or simply excluded by the user. Installing the app on another drive does not redirect Windows' temporary directory or profile defaults.

Source inspection at `e493113` found:

| Area | Current behavior and source |
| --- | --- |
| Default data folder | Local AppData until a saved folder overrides it (`MainViewModel.cs`). |
| Models, private inference runtime, outputs, jobs and normal worker logs | Under the saved data folder. Setup explicitly places uv's cache there, and inference explicitly sets `HF_HOME` there (`inference_setup.py`). |
| Worker temporary files | The C# supervisor and Python setup/inference children inherit Windows temp settings (`PythonWorkerClient.cs`, `inference_setup.py`). This is an escape route; inspection alone does not establish which dependencies wrote there or how many bytes. |
| Painted-area hand-overs | Windows temp, including the MCP path (`Housekeeping.cs`, `McpServer.cs`). |
| Update installer downloads | Windows temp (`Housekeeping.UpdateFolder`, `UpdateService.cs`). |
| Preferences and startup error logs | Local AppData (`MainWindow.xaml.cs`, `SettingsStore.cs`, `App.xaml.cs`). Startup logs rotate at about 1 MiB; this is distinct from large inference storage. |
| Application installation | Defaults to Local AppData's Programs folder (`packaging/media-workbench.iss`). Installer extraction/staging must also be audited. |

These are confirmed code paths, not a trace of the user's disk activity. No files were moved or removed during this audit, and no runtime containment or migration has been verified.

## Intended experience

1. Before any large download or job, choose **Where Media Workbench works**. Show available drives, free space, drive type, and the space the operation needs. Explain that this location holds models, pictures/videos, and temporary work. Remember an existing saved choice. Do not rank an SSD ahead of the user's exclusions or available capacity, hard-code drive letters, or silently choose another volume.
2. Use that work folder for every app-controlled large write: downloads, extraction, package/model caches, dependency scratch files, image hand-overs, video frames, update staging, and normal logs as well as models and results. Temporary work defaults to a managed subfolder there; a different temporary-work location is an Advanced option requiring an explicit choice and its own space check.
3. If the location is full, disconnected, unavailable or unwritable, stop before heavy work with a plain explanation and choices to free space, reconnect it, or choose another location. Never fall back to Windows temp, the user profile, or a different drive without the user choosing it. A restart, worker retry, repair, MCP operation, or app update must obey the same rule.
4. Settings should show the actual application, work, temporary, cache and preferences locations, their volumes, and what is retained. Advanced options should support an explicit restriction on which volumes may receive managed work; reading a user-chosen source and saving an explicitly chosen export are separate intentional actions. Do not force a beginner to configure environment variables.
5. Support keeping the application on the chosen drive as well: retain the installation destination on upgrade and expose the destination clearly during installation. Decide and document the bounded profile footprint (preferences/location pointer and startup diagnostics). Provide a supported way to keep app-owned settings and logs with the chosen location for users who want all app-owned files there; do not advertise this as working until launch, update and uninstall have been tested with it.

The promise covers Media Workbench and its owned processes. Windows may independently write paging, driver caches, crash reports, installer bookkeeping or security/antivirus data on the system drive. Measure and describe these separately rather than promising that Windows will never write there. Avoid changing global Windows temp, cache or paging settings.

## Implementation boundaries

- Centralize the saved storage policy and resolved locations for GUI, CLI/MCP, updater, supervisor and Python children. Rust should own managed path/volume validation and space policy behind the versioned C ABI; C# coordinates presentation/processes and Python remains out of process. Reject paths/links that escape the permitted managed locations; preserve the existing reparse-point protections. Check actual volumes, including mounted folders, rather than trusting a drive-letter prefix.
- Set process-scoped temporary and supported dependency cache locations before launching owned processes or importing dependencies. Audit `TEMP`, `TMP`, Python temporary-directory caching, uv/package extraction, Hugging Face, PyTorch/CUDA-related caches and SQLite temporary storage. Respect isolated Python behavior; environment changes alone are not evidence that each dependency obeys them. Carry the policy to every child and test actual writes.
- Check writable destinations and worst-case peak space on every volume receiving bytes, including compressed archives plus extracted files, updates and temporary frames. Aggregate simultaneous needs when locations share a volume. Keep a reserve and check during long writes; handle races, cancellation and disappearing drives without a second-location fallback. Drive speed can inform advice, but capacity and the user's choice take precedence.
- Keep bounded clean-up under the existing ownership/locking rules. Clean managed scratch after success/cancel/crash, retain retry downloads only under the documented policy, and protect images, videos, job records and private inputs required by Retry or **Use settings**. Legacy temp/profile cleanup must identify this app's own files precisely; do not sweep other applications' caches.
- Add an explicit **Move existing data** workflow. Today changing Settings only changes the path. Preflight destination space, block conflicting operations, copy and verify, repair recorded paths/index references, and switch settings atomically only after success. Keep the source intact until the verified switch and make source removal explicit. Interrupted moves must recover without data loss or silent redownloads. File/runtime/inference verification gates still apply; do not carry readiness forward unless its evidence remains valid for the resulting configuration.

## Release acceptance

- Headless checks exercise all location consumers, fake low-space/unwritable/missing volumes, shared-volume peak-space accounting, child environment propagation, containment/reparse refusal, and no automatic fallback. Include fresh/default setup, existing saved settings, CLI/MCP, discovery/download, repair, updates, and settings round trips. Do not test only the helper that constructs the paths.
- Migration checks cover cancellation/crash at each stage, conflicting workers, insufficient space, preserved original data, unchanged picture/video checksums, job/input/settings reuse, index rebuild, and honest readiness after the move.
- On a two-volume Windows test machine, explicitly select the larger non-system volume and exclude the system volume for managed work. Trace process-tree file writes during real setup (including package extraction), base/SDXL/video verification and generation, upscaling, painted changes through GUI and MCP, update download/install/relaunch, cancellation and crash recovery. Record actual outside-volume writes and peak space. Headless environment assertions are not proof of dependency behavior.
- Exercise disconnected/low-space work drives without filling the user's real system drive. Check fresh installation plus upgrade/uninstall on Windows 10/11, both the normal preferences arrangement and the supported all-app-owned-files arrangement. Installation/update temp handling is part of the gate, not a deferred exception.
- Run `./scripts/check.ps1`, `./scripts/build.ps1`, and published headless checks. Interactive and GPU checks require the user's availability; do not launch them during the pause. Record results in `docs/validation.md` and update README, current status and handoff before the version/changelog push triggers the Actions release.

## Resume here

First implement the shared location policy and propagate it through the worker/update/painted-hand-over paths, with meaningful headless checks. Then complete migration, installer/update behavior, and real write-trace acceptance before releasing the milestone. Use ignored build/check output on a suitable development volume; development tools' own caches are a separate audit from the shipped application.
