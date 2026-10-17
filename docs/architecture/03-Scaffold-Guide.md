# Media Workbench — scaffold and validation guide

17 October 2026 • development source, not an installer or a finished generation application

## 1. What this scaffold establishes

The scaffold makes the architecture concrete: a dark WinUI 3 shell, a UI-independent C# core, infrastructure adapters, a direct CLI, a separate Python process, and a declarative recipe boundary. The process client and worker communicate through actual UTF-8 JSON lines. No model weights or inference packages are required to exercise this boundary.

The working name is **Media Workbench**. It can be changed before product implementation.

### Implemented and checked

| Area | Actual behavior |
|---|---|
| Windows app | Builds as an unpackaged, self-contained Windows x64 application; starts a desktop window |
| Navigation | Home, Discover, Installed, Create, Enhance, Jobs, Library, Tools, Settings |
| UI content | Pages explain their intended purpose; local draft catalog and installation-plan preview |
| C# core | Protocol contracts, recipe/plan records, worker/planner interfaces, guarded job transitions |
| Python transport | Real child process, handshake, structured requests/events, stdout/stderr separation |
| Progress/cancellation | Demonstration streams progress and cooperatively cancels; client has forced-stop fallback |
| Worker health | Reports Python and read-only NVIDIA detection; explicitly states inference is unavailable |
| Native adapter | Read-only ffprobe version probe using an explicit executable path |
| Direct access | CLI for health, local catalog, plan preview, and the protocol demonstration |
| Example recipe | SD1.5 baseline candidate, clearly draft with missing revision/profile |
| Schemas | JSON Schema documents for the scaffold recipe and wire envelopes |
| Checks | Five Python tests and fourteen C# behavioral/integration checks passed on this PC |

### Intentionally absent

There is no live Hub search, account integration, model download, dependency installer, Diffusers execution, image generation, video generation, persistent database, durable scheduler, completed-media gallery, automatic repair, installer, or updater. Navigation pages are a shell for those features. The planning code always refuses installation because the executor is absent.

The demo writes a uniquely named **text file** with an explicit demonstration label. It never produces a fake generated image. Health reports detection rather than claiming that the GPU can run an inference profile.

## 2. Source structure

```text
scaffold/
  MediaWorkbench.slnx
  global.json                     .NET 10 SDK family
  Directory.Build.props           common compilation settings
  NuGet.Config                    explicit nuget.org source
  src/MediaWorkbench.App/          WinUI project
  src/MediaWorkbench.Core/         contracts and domain state
  src/MediaWorkbench.Infrastructure/
  src/MediaWorkbench.Cli/
  workers/python/media_worker.py
  recipes/sd15-draft.json
  schemas/recipe.schema.json
  schemas/protocol.schema.json
  tests/MediaWorkbench.Checks/
  tests/test_worker.py
  scripts/check.ps1
  scripts/run-desktop.ps1
```

The WinUI project originated from Microsoft's installed NavigationView template and was adapted to the proposed application. It pins Windows App SDK **2.5.1** and Windows SDK BuildTools **10.0.28000.2705**, the stable package versions resolved during scaffolding. It targets `net10.0-windows10.0.26100.0`, has a Windows minimum platform value of `10.0.19041.0`, and builds for x64. The actual package-supported OS baseline still needs clean-machine validation before release.

The shell is self-contained for its .NET/Windows App SDK dependencies. That setting does not include the Python runtime or any AI dependencies. The development launcher uses a supplied interpreter; the product architecture calls for managed runtime provisioning later.

## 3. Prerequisites and first commands

Use Windows x64 with .NET SDK 10 and Python 3.12 or later. Python 3.12 is the proposed initial generation baseline; the scaffold itself uses only standard-library facilities. First .NET restore/build needs internet access for NuGet packages. Visual Studio is optional for the checked command-line build.

Open PowerShell in the `scaffold` directory. Replace the interpreter path with the Python executable on your computer. The commands below are examples for manual execution; they are not a proposed everyday product workflow.

### Check the boundary without opening a GUI

```powershell
.\scripts\check.ps1 -Python 'C:\path\to\python.exe'
```

Optionally test an existing ffprobe executable:

```powershell
.\scripts\check.ps1 -Python 'C:\path\to\python.exe' -Ffprobe 'C:\path\to\ffprobe.exe'
```

The script writes build and test artifacts under `.runs/checks` by default. Supply `-Artifacts 'D:\chosen\development-output'` to use another location. It does not install Python packages, download models, or modify an existing AI environment.

### Build the whole solution

```powershell
dotnet build .\MediaWorkbench.slnx
```

### Open the desktop scaffold

```powershell
.\scripts\run-desktop.ps1 -Python 'C:\path\to\python.exe'
```

The launcher sets application-specific environment values for its own child launch and restores the previous values in its PowerShell process afterward. It does not persist system/user environment changes. Default build output is `.runs/desktop-build`.

On Home, choose **Check connection** or **Run protocol demo**. Discover displays the local draft recipe and **Preview installation plan**. Settings exposes the development root and interpreter path. These developer connection controls will be replaced by managed provisioning in the product.

### Use the direct CLI

```powershell
dotnet run --project .\src\MediaWorkbench.Cli -- 'C:\path\to\python.exe' $PWD.Path health
dotnet run --project .\src\MediaWorkbench.Cli -- 'C:\path\to\python.exe' $PWD.Path catalog
dotnet run --project .\src\MediaWorkbench.Cli -- 'C:\path\to\python.exe' $PWD.Path plan
dotnet run --project .\src\MediaWorkbench.Cli -- 'C:\path\to\python.exe' $PWD.Path demo 'D:\chosen\demo-output'
```

The demonstration requires an explicit absolute output path. Ctrl+C requests cancellation. The client is intentionally limited to one active request; the future application scheduler owns the larger queue.

## 4. How to follow the code

1. `Core/Contracts.cs` defines data and service boundaries.
2. `Infrastructure/PythonWorkerClient.cs` starts the owned process and validates communication.
3. `workers/python/media_worker.py` implements the other side of the protocol.
4. `Infrastructure/InstallationPlanner.cs` explains why the draft recipe cannot install.
5. `Infrastructure/NativeToolProbe.cs` illustrates a narrow native executable adapter.
6. `App/MainWindow.xaml` and its code-behind compose the development UI.
7. `tests/MediaWorkbench.Checks/Program.cs` exercises the real boundaries.

The UI currently uses code-behind for its small development demonstration. Production pages should introduce view models and application services as real workflows are implemented. This scaffold does not falsely label code-behind as a completed MVVM architecture.

## 5. Protocol behavior

The worker emits a `hello` event before accepting work. It supports `health`, `catalog.list`, and `demo.run`. A `cancel` envelope reuses the active request ID. Cancellation has no separate response: the operation produces the terminal error with code `cancelled`, or a result if completion already won the race.

Requests and events are versioned. JSON goes on stdout; diagnostic text goes on stderr. Generated media will eventually travel through file paths, not base64 JSON payloads. The current demonstration's artifact is a plain text file.

The C# client drains stderr while consuming stdout, checks request IDs and protocol versions, and refuses an invalid handshake. After an invalid or partially consumed transport it stops that owned process rather than reusing it. Cooperative cancellation allows three seconds before the forced-stop fallback. Disposing a client assumes its active request has been canceled and awaited.

## 6. What was verified on 17 October 2026

### Full solution

`dotnet build` completed successfully for all five projects, with **zero warnings and zero errors**. Build output was redirected to the task's `work/build` folder so compiled binaries are outside the delivered source package.

### Python checks — 5 passed

Non-object request rejection; protocol-version rejection; explicit unsupported generation; relative output directory rejection; cancellation without writing a result artifact.

### C# checks — 14 passed

Real Python handshake; truthful health; draft catalog; Unicode and space-containing paths; streamed progress with one terminal result; cooperative cancellation; reuse after cancellation; explicit unimplemented generation; invalid output rejection; blocked draft installation; terminal job guard; incompatible handshake rejection; unexpected worker exit reporting; real read-only ffprobe invocation.

### Desktop verification

The compiled application was launched successfully, and Windows reported its window titled **Media Workbench — architecture scaffold**. Computer Use was then stopped with the physical Escape key before a screenshot or control interaction completed. **Visual layout, navigation, and button behavior have not been manually verified.** No screenshot is presented as evidence. No further desktop interaction was performed after that stop.

### Additional checks

Delivered JSON files parse successfully. The schemas are design documents; full schema-validation execution is not part of the current standard-library-only check suite. No account token was read, no Hugging Face account was changed, and no model or inference environment was downloaded or installed. Building the C# source did restore development NuGet dependencies.

## 7. Known scaffold limitations

- No database, cross-instance lock, persistent queue, restart reconciliation, or resource lease implementation.
- No Windows Job Object integration; process-tree termination is only the development fallback.
- C# checks event size after reading a line. Production needs a bounded streaming parser.
- The demo accepts a caller-selected absolute output folder. Production requires stricter owned-root, link, and artifact finalization rules.
- Recipe deserialization is not complete schema enforcement or cryptographic catalog verification.
- The prototype has no token store, approved tool downloader, archive extraction, or license-notice assembly.
- Process output and diagnostic retention need production hardening for large workloads.
- A successful health check does not verify CUDA inference, package compatibility, visual quality, or usable speed.
- No automated GUI tests or completed manual visual QA exist yet.

These are explicit engineering tasks, not hidden assumptions behind an Install button.

## 8. Next implementation boundary

Implement a lightweight Hub discovery profile, real search/detail handlers, and storage selection. Keep model loading outside that profile. Select and test the first exact inference profile, resolve a pinned model revision, and record the target-GPU benchmark. Then connect a typed installation executor and the SD1.5 inference adapter.

Before adding a broad catalog, demonstrate the complete vertical slice in **02-Application-Map.md**. Before shipping, meet the clean-machine and recovery acceptance criteria in **01-Technical-Deep-Dive.md**.
