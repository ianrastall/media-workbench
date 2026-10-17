# Media Workbench — technical deep dive

Architecture proposal • 17 October 2026 • working name, not a final product name

## 1. What we are building

A Windows desktop application for discovering, installing, running, and maintaining local image and video tools. The normal interaction is choosing a task and supplying creative inputs. The application manages the model files, runtimes, dependencies, jobs, and outputs. An advanced view exposes the actual parameters, file locations, recipe versions, and diagnostic information.

The proposed foundation is **C# / WinUI 3 for the application, Python / Hugging Face / Diffusers for model operations, and selected native executables for specialized processing**. This is an application that coordinates existing engines. Its own substantial engineering work is orchestration, installation recipes, compatibility evidence, a coherent interface, and recovery.

This document specifies the intended application. The accompanying scaffold implements a narrow, explicitly identified subset. Statements labeled **decision** are architectural recommendations. **Verified** facts come from local inspection or the linked primary documentation. **Open** items require implementation or measurement.

### Product requirements

- A dark interface that remains usable while the generation environment is absent or broken.
- Discover models through Hugging Face and understand what can actually run locally.
- Install a supported model and its dependencies through one application operation.
- Report meaningful progress across download, installation, loading, generation, and export.
- Retain direct access to lower-level controls through an advanced panel and documented worker commands.
- Reuse well-defined packages, libraries, and executables; add integrations only for concrete tasks.
- Keep model files, environments, application updates, and user outputs separately manageable.
- Recover from interrupted work without falsely showing incomplete installations as ready.

### Scope boundaries

The initial release targets one local Windows x64 user and one discrete GPU. It includes text-to-image, model lifecycle management, job history, and a small set of processing tools. Image-to-image, inpainting, and video are subsequent recipe-backed capabilities. A general visual workflow editor, training platform, remote render farm, and arbitrary plugin marketplace would substantially increase scope and are deferred.

“Install automatically” means executing a known installation plan. A random model repository is insufficient evidence that the application can install and run it. The wider Hub remains browsable; supported installation is an additional capability supplied by our application.

## 2. Machine baseline and implications

Verified by read-only local inspection during this discussion:

| Item | Observed | Consequence for this design |
|---|---|---|
| GPU | NVIDIA GTX 1660 Ti, 6,144 MiB | Start with conservative image workloads; serialize GPU jobs |
| Integrated graphics | Intel UHD Graphics 630 | Do not automatically use this for the initial generation profile |
| System RAM | 64 GB | CPU offload is possible, but does not imply acceptable performance |
| Python | Python 3.12 available; Python 3.14 also registered | Select the supported interpreter explicitly |
| Package manager | `uv` available | Use it for controlled environments and lock files |
| .NET | SDK 10.0.113; SDK 8 also present | Use .NET 10 for the scaffold |
| Native media tools | FFmpeg and ffprobe found on PATH | They can be probed; no copy or redistribution has been validated |
| Free space, rounded snapshot | C: 35 GB; D: 387 GB; F: 296 GB; G: 1,408 GB | Let the user select a storage root before large downloads |

These observations are a point-in-time baseline, not global requirements. Drive letters and available space must be rediscovered on each computer. Existing tools are detected without taking ownership of them. The production installer should support its own managed runtime and work without the developer tools present on this PC.

**Decision:** propose a user-chosen storage folder on a drive with sufficient space. Do not automatically select or modify an unrelated folder because its drive is roomy. The scaffold writes only to explicitly supplied output paths or its own development run directory.

**Open:** an actual Diffusers / PyTorch version pair, precision choice, driver compatibility, safe resolutions, timings, and peak memory must be measured on the 1660 Ti. Hardware names and model tags alone cannot certify a profile. Older GPU architectures should not inherit BF16, FP8, or optimized-kernel assumptions from modern examples.

## 3. Why the hybrid architecture

WinUI 3 supplies native Windows controls and the Fluent design system for C# applications. Diffusers is a Python library with complete generation pipelines and access to their components. Those documented capabilities support the proposed split. [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/), [Diffusers pipelines](https://huggingface.co/docs/diffusers/using-diffusers/loading)

| Approach | Strength | Cost | Decision |
|---|---|---|---|
| All Python, Qt GUI | One language; quick experiments; portable UI | UI deployment and generation environments still need management | Viable alternative if portability becomes a priority |
| C# GUI plus Python workers | Native Windows experience; GUI independent of AI environment | Two codebases and a process protocol | Recommended |
| C# UI and entirely native inference | Fewer Python dependencies for supported engines | Coverage and model compatibility depend on the selected engine | Support as an optional adapter |
| GUI automation over another application | Can reuse an existing visual workflow | Integration depends on another app's interface and lifecycle | Outside the proposed foundation |
| Reimplement inference and downloads | Complete control | Large implementation and maintenance burden | Reuse existing engines instead |

The C# choice does not improve diffusion speed by itself. Its value is application structure and Windows integration. Python remains necessary for the selected generation ecosystem unless a particular task uses a different engine.

## 4. Layers and dependency direction

### Presentation: `MediaWorkbench.App`

Owns navigation, views, view models, accessibility, previews, and display state. Uses application services through interfaces. It does not invoke package managers, import Python, construct model pipelines, or parse native-tool logs directly. UI updates are marshaled to the WinUI dispatcher and long work never runs on the UI thread.

### Domain and application: `MediaWorkbench.Core`

Owns job states, capability descriptors, installation plans, recipe identity, validation results, and service contracts. It references neither WinUI nor PyTorch. An operation such as InstallModel produces a plan, validates prerequisites, schedules execution, and commits readiness only after verification. A generation job captures immutable settings before it enters the queue.

For the scaffold this is one small project. Split domain and application projects only when the code warrants the distinction; avoid a directory structure that requires dozens of empty assemblies.

### Infrastructure: `MediaWorkbench.Infrastructure`

Implements process supervision, transport, file storage, catalog loading, tool probing, and eventually SQLite persistence and credential access. It translates external failures into application error codes. It starts workers by absolute executable path with structured argument lists and no shell interpretation. .NET documents `ArgumentList` for argument handling. [ProcessStartInfo.ArgumentList](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist)

### Python worker: `workers/python`

Exposes capabilities and performs approved operations. Planned modules include Hub catalog access, downloads, device probes, pipeline loading, generation, and artifact metadata. Generation packages are imported lazily, so metadata browsing can remain available when an inference environment is unavailable. Optional dependencies belong to explicit runtime profiles.

### Native tool adapters

An adapter translates a task to a particular executable's arguments, knows how to interpret progress, validates expected output, and owns tool-specific cancellation and exit-code handling. C# may invoke native tools directly. A tool must not be routed through Python merely for architectural uniformity.

Dependency direction: **App → Core contracts; Infrastructure → Core; App composes Infrastructure; worker speaks the protocol, not C# internals.**

## 5. Process topology and lifecycle

**Decision:** use local child processes with UTF-8 newline-delimited JSON over redirected standard input/output. Keep diagnostics on stderr. Do not start a localhost web server for the initial desktop-only use case. This removes ports, service discovery, and browser authentication from the first implementation.

One application instance owns a scheduler. A lightweight Hub worker and a GPU inference worker may use separate runtime profiles. Keep an inference worker alive for consecutive jobs using the same model where memory permits. Release its pipeline, or stop the process, before another GPU tool acquires the device lease. Metadata operations should not import PyTorch or occupy GPU memory.

The worker starts, emits a protocol handshake with capabilities, accepts requests, emits progress and terminal responses, and shuts down when its input stream closes. The supervisor drains stdout and stderr concurrently. Broken pipes, unexpected EOF, incompatible protocol versions, and malformed envelopes become explicit failures.

### Ownership and termination

The application owns its child processes, not every Python or FFmpeg process on the computer. Cancel cooperatively first; after a bounded grace period, terminate the owned process group and mark the operation canceled or interrupted according to the observed outcome. Releasing a worker must also release its GPU lease.

The scaffold uses .NET process-tree termination as its forced-stop fallback. Production hardening should assign workers to Windows Job Objects with kill-on-close semantics, including handling assignment races and nested-job environments. Job Objects manage process groups; waiting for a parent process does not establish that every descendant has exited. [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), [Process.Kill](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill)

### Concurrency rules

- One GPU-heavy job at a time on the initial profile.
- A bounded number of network transfers; do not spawn one worker per model file.
- CPU export may overlap downloading, subject to disk and RAM pressure.
- The scheduler owns leases across all engines, including native Vulkan tools.
- A second application instance must eventually attach to the first or be refused for the same storage root.

The scaffold has one active request per worker client. A persistent application-wide queue and single-instance locking are later implementation work.

## 6. Interoperability contract

Every request has `protocol`, `id`, `operation`, and `payload`. Every event has `protocol`, `id`, `kind`, and `payload`. A request gets exactly one terminal result or error. Progress is nonterminal. A transport crash can prevent a terminal event; the supervisor synthesizes an interrupted outcome.

Example request:

```json
{"protocol":1,"id":"job-123","operation":"generate.image","payload":{"installationId":"sd15-local","prompt":"a quiet woodland","seed":123,"width":512,"height":512,"steps":20,"outputDirectory":"D:\\MediaWorkbench\\jobs\\job-123"}}
```

Example progress and completion:

```json
{"protocol":1,"id":"job-123","kind":"progress","payload":{"stage":"denoise","completed":8,"total":20,"unit":"steps"}}
{"protocol":1,"id":"job-123","kind":"result","payload":{"artifacts":[{"path":"image.png","mediaType":"image/png"}],"metadataPath":"generation.json"}}
```

`generate.image` is a planned operation; the scaffold deliberately returns `not_implemented` for it. Its runnable demonstration is separately named `demo.run` and creates a text artifact, never a pretend generated image.

Use bytes for downloads, steps for denoising, and frames for frame processing. Report unknown totals as indeterminate. Percentages from unlike stages are not directly additive. An overall estimate needs recorded stage weights and must be labeled approximate.

### Versioning and validation

- Handshake declares protocol version and supported operations; clients reject incompatible versions.
- Validate operation payloads before side effects and use stable machine-readable error codes.
- Constrain message sizes and log retention. Production needs bounded incremental line reads; the scaffold's simpler reader is documented as incomplete hardening.
- Resolve artifact references relative to the assigned job directory and reject escapes, including Windows path and link edge cases.
- Keep binary images/video on disk. JSON carries references and metadata.
- Never put account tokens in command-line arguments, event payloads, or logs.
- A cancel request identifies the active job. A completion/cancel race has exactly one recorded terminal outcome.

Suggested errors: `authentication_required`, `access_pending`, `unsupported_recipe`, `profile_unverified`, `disk_full`, `download_failed`, `dependency_install_failed`, `gpu_unavailable`, `out_of_memory`, `invalid_input`, `tool_failed`, `worker_crashed`, and `protocol_mismatch`.

## 7. Model discovery and compatibility

Use `huggingface_hub` for model search and repository metadata. Search results are discovery records, not installation instructions. Tags can be missing, stale, or user-authored. Filter by task and library when available, then enrich a selected result with repository files and the application recipe registry. [Hub search](https://huggingface.co/docs/huggingface_hub/guides/search)

The interface separates three concepts:

| Concept | Example values | Meaning |
|---|---|---|
| Integration support | Supported / experimental / browse only | Does this app have an adapter and recipe? |
| Machine fit | Verified / estimated / unknown / incompatible | What evidence exists for this hardware/profile? |
| Installation state | Not installed / installing / ready / needs repair | What is actually on this machine? |

Do not combine these into a single green compatibility badge. A supported model may be too large for this computer. A model that fits in memory may still be untested or unacceptably slow.

A model detail view shows its publisher, task, source link, file format, relevant license, planned download, storage estimate, supported actions, runtime profile, and compatibility evidence. Missing estimates are shown as unknown. Expected duration should come from local benchmark records with resolution, precision, frame count, and engine version.

### Hugging Face authentication

Public browsing and available public downloads should work without a token. Gated/private resources need authenticated access; gated access may require a browser acceptance or author approval. The application can take the user to that page and resume once access is available. It cannot legitimately automate a publisher's approval decision. [Gated model access](https://huggingface.co/docs/hub/models-gated)

**Decision:** store any application-owned token using a Windows-protected credential facility. Give the worker a short-lived inherited environment value or another protected local channel. Avoid writing a second plaintext login cache by default. Use a read-only, appropriately scoped token where it suffices. Signing out removes this application's credential; existing external credentials are not silently deleted.

The scaffold does not authenticate, inspect existing tokens, or connect to the user's account.

## 8. Recipes: the core of dependable automation

A recipe is versioned declarative data owned by the application. It names a known adapter, source repository, immutable model revision, selected files, runtime profile, capabilities, parameter schema, resource envelope, and verification steps. A recipe never embeds shell strings or arbitrary Python source.

Recommended recipe fields:

| Group | Fields |
|---|---|
| Identity | schemaVersion, id, version, displayName, owner |
| Model source | provider, repoId, revision, allowPatterns, expected files |
| Execution | adapterId, runtimeProfileId, supported operations |
| Files | format, artifact roles, optional hashes, expected byte counts |
| Hardware | architecture constraints, tested GPU, memory observations, precision |
| Inputs | parameter types, ranges, defaults, advanced controls |
| Verification | file check, import probe, device probe, small inference check |
| Distribution | source URL, license reference, notices, redistribution decision |
| Status | draft / experimental / verified; evidence reference and date |

An immutable revision prevents a model publisher's subsequent update from silently changing an installed model. Download only the needed files and format. A repository containing PyTorch, ONNX, TensorFlow, and multiple precision variants should not be downloaded indiscriminately. The Hub download API supports selecting files and revisions. [Hub downloads](https://huggingface.co/docs/huggingface_hub/guides/download)

**Recipe promotion:** draft → schema-valid → files resolved → environment reproducible → inference smoke test → hardware benchmark → verified. Each promotion records evidence. A change to model revision, adapter, package lock, or meaningful performance setting invalidates the relevant verification.

The sample SD1.5 recipe in the scaffold is a **draft**. It intentionally has no approved runtime profile or immutable model revision. Planning can explain what is missing, while installation remains unavailable. This avoids manufacturing a compatibility claim before generation has been tested.

## 9. Installation engine

The application builds an installation plan before executing it. The plan is an ordered set of typed actions and prerequisites, with estimates and an installation identity. Execution is resumable at safe boundaries; “resume” does not mean running a partially completed command again without understanding its effects.

### Planned transaction

1. Validate recipe, source, storage paths, and selected operation.
2. Check access, runtime support, driver/device status, free space, and already cached files.
3. Resolve an approved runtime profile and acquire the installation lock.
4. Create a new versioned environment in its final location; do not move virtual environments after creation.
5. Install the exact dependency lock into that environment.
6. Retrieve the pinned model files through the Hub cache.
7. Verify required files and profile imports; run the intended hardware probe.
8. Run a bounded model smoke test where required by the recipe.
9. Commit the installation record as ready in one metadata transaction.
10. Release locks and retain a concise installation report.

Model caches and package managers are not one transactional database. Recovery reconciles durable records with actual files and environment health. Never claim a distributed rollback that these tools do not provide.

### Python environments

Use `uv` and a tested Python version to create app-managed environments. Share an environment among recipes only when they use the same approved profile fingerprint. Each fingerprint includes interpreter, platform, package lock, engine, and GPU backend. Create replacement environments alongside existing ones so an update does not damage the previous working installation.

`uv` can manage Python distributions, and its documented lock/sync flow provides reproducible dependency sets. A lock must also account for the intended PyTorch wheel source and target platform. [uv Python management](https://docs.astral.sh/uv/concepts/python-versions/), [uv locking and synchronization](https://docs.astral.sh/uv/pip/compile/)

For production support, prefer tested wheels and fail clearly when an unexpected source build is required. Do not quietly download a C++ toolchain. The first install's large torch/runtime downloads are visible stages, with disk estimates. Importing torch successfully is not sufficient verification that CUDA kernels work on this GPU.

### Failure and repair

| Failure | Intended behavior |
|---|---|
| Network interruption | Keep the operation resumable through the supported downloader; retry with bounded backoff |
| Access denied | Preserve plan; explain login, license acceptance, or pending access |
| Disk exhausted | Stop writes; identify storage root and required headroom; keep completed outputs |
| Dependency conflict | Retain environment report; preserve prior ready environment |
| Import failure | Mark environment unhealthy; rebuild from known lock |
| Smoke test OOM | Keep files cached; mark profile unsuitable for that workload |
| App closes during install | Next launch marks the attempt interrupted and reconciles it |
| Unexpected worker exit | Record exit and diagnostic tail; release owned resources |

## 10. Storage ownership and persistence

The Hub already provides shared caching and tools for inspecting/removing cached revisions. Reuse that machinery instead of inventing a second blob store. Windows symlink support can affect physical storage efficiency, so estimates must reflect the actual filesystem behavior. [Hub cache design](https://huggingface.co/docs/huggingface_hub/guides/manage-cache)

Proposed layout, with the data root selected in the application:

```text
<application installation>/       signed GUI and shipped worker source
<local app data>/MediaWorkbench/  small configuration, local database, logs
<selected data root>/
  hub/                            application-managed HF cache
  environments/<profile-hash>/    immutable-in-use Python environments
  tools/<tool>/<version>/          application-managed native binaries
  jobs/<job-id>/                   staging, temporary files, job manifest
  library/<artifact-id>/           completed media and provenance
  reports/                        installation and benchmark evidence
```

Set cache-related environment variables for owned workers only. Avoid changing global system/user environment values during normal setup. Hugging Face documents explicit cache configuration and offline controls. [Hub environment variables](https://huggingface.co/docs/huggingface_hub/package_reference/environment_variables)

### SQLite records

**Decision:** a local SQLite database stores recipes referenced by version, installations, runtime profiles, jobs, job events, artifacts, tool installations, benchmark evidence, and schema version. Large media stays in files. The C# application is the only database writer; workers report events.

Use transactions for state transitions and foreign keys for ownership. WAL mode can support readers alongside a writer, but checkpointing and backup must be designed. Keep the live database on a local filesystem; the SQLite WAL documentation explains its shared-memory/local-machine requirements. [SQLite WAL](https://www.sqlite.org/wal.html)

Backups use a supported SQLite backup mechanism or a coordinated closed-database snapshot. Copying only the `.db` file while WAL writes are active is not a sufficient backup strategy. Schema migrations are versioned and preceded by recoverable backups.

### Cleanup

Separate “remove installation,” “remove cached model files,” and “delete outputs.” Deleting an installation releases references; shared cache content is removed only when safe and intentionally selected. Never recursively delete paths obtained from arbitrary repository metadata. Resolve and verify owned roots, reject link escapes, and refuse broad drive/root targets. External imported model folders remain externally owned by default.

The scaffold has no destructive cleanup or model deletion implementation.

## 11. Job execution and generation

### Job record

Store operation, immutable input parameters, recipe/version, resolved model revision, runtime fingerprint, adapter version, input artifact IDs, seed, execution device, timestamps, status, output paths, and error details. User-facing names can change; IDs cannot.

### State machine

```text
Queued → Preparing → Running → Finalizing → Succeeded
   │         │          │          │
   └─────────┴──────────┴──────────┴──→ Failed
   └─────────┴──────────┴──────────┴──→ Cancelling → Cancelled
```

After a crash, nonterminal persisted jobs become **Interrupted**, with retry offered as a new attempt. A retry never overwrites a completed artifact. Recoverable installation tasks may resume verified stages; a half-completed diffusion run is generally restarted unless an adapter specifically supports checkpoints.

### Generation path

Validate dimensions and inputs → resolve ready installation → acquire GPU → start/reuse inference worker → load pinned local pipeline → apply verified profile → denoise → decode → write temporary media → validate → atomically promote output → commit artifact and provenance → release GPU.

Keep exact API calls inside a versioned adapter. Diffusers has pipeline-specific behavior; avoid a universal function that accepts every possible model parameter. Discover available operations through adapter capabilities and render only supported controls.

Diffusers supports callbacks during denoising, which can feed progress and cooperative cancellation where the chosen pipeline supports them. Loading and decoding may lack the same interruption points; the application must display those stages honestly. [Pipeline callbacks](https://huggingface.co/docs/diffusers/using-diffusers/callback)

### Memory policy

Batch size one for the initial 6 GB profile. Establish a tested precision and resolution first. Evaluate CPU offload and VAE tiling individually; these are model-specific tradeoffs. Do not enable incompatible attention optimizations together or assume that quantization permits every model to run acceptably. Diffusers documents several memory techniques and their limitations. [Memory optimization](https://huggingface.co/docs/diffusers/optimization/memory)

After OOM, stop the failed attempt cleanly. Offer a specific lower-memory preset or smaller workload supported by the recipe. Preserve the original settings; do not silently change dimensions or quality and label the result equivalent.

### Reproducibility

Save settings and engine/model identities next to every result. A seed alone cannot guarantee identical pixels across devices, precision, package versions, and kernels. Store enough provenance to recreate the workload and explain meaningful differences.

## 12. Native tools as first-class components

| Candidate | Role | Integration boundary | Initial priority |
|---|---|---|---|
| FFmpeg / ffprobe | Media inspection, frame extraction, encoding, audio muxing | CLI; ffprobe structured output; FFmpeg progress parser | First video/export integration |
| libvips | Thumbnails, resizing, image conversion | Library binding or CLI behind one adapter | Evaluate against ordinary .NET imaging needs |
| Real-ESRGAN NCNN Vulkan | Image/frame upscaling | CLI and approved model files | Optional image enhancement |
| RIFE NCNN Vulkan | Frame interpolation | Frame files and CLI | Later video enhancement |
| stable-diffusion.cpp | Alternative inference engine | CLI/library with its own supported model formats | Later experiment; optional alongside Diffusers |

These are candidates, not certified bundled dependencies. Each has its own maintenance, format, and GPU requirements. Sources: [FFmpeg](https://ffmpeg.org/documentation.html), [libvips](https://github.com/libvips/libvips), [Real-ESRGAN NCNN Vulkan](https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan), [RIFE NCNN Vulkan](https://github.com/nihui/rife-ncnn-vulkan), [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp).

### Adapter contract

Describe executable identity/version, capabilities, required model files, accepted inputs, output validation, progress parsing, resource class, and cancellation behavior. Store arguments as structured values. Use a working directory inside the job root. Capture stderr separately and validate an output even when the executable exits with zero.

Tool discovery checks a supplied path and version. A detected external installation can be used with explicit configuration but is not upgraded or deleted by the application. An app-managed tool can be installed side by side by version. Downloads use fixed source identities and verified hashes where supplied by the approved distribution manifest.

### Video chain

Probe source → decode selected frames → process frames → encode staging video → restore/mux audio → probe completed file → promote artifact. Preserve frame order, dimensions, timing, color interpretation, audio synchronization, and output settings. Interpolation across scene cuts needs adapter-specific handling. Variable frame rate and HDR should be initially unsupported or explicitly normalized through a defined export profile.

Frame directories can be much larger than the compressed input. Estimate temporary-space requirements before extraction, and remove only owned temporary files after successful finalization or an explicit cleanup action. Upscaling and interpolation can alter detail or introduce artifacts; retain originals and save the processing recipe.

### Redistribution

Track executable and model licenses independently. FFmpeg's applicable obligations depend on its build configuration; shipping an arbitrary downloaded binary requires checking that exact build. This is a release packaging decision, not a popup in every creative task. [FFmpeg licensing](https://ffmpeg.org/legal.html)

## 13. Security and trust boundaries

These boundaries are part of an application that downloads executable components and processes account credentials:

- Prefer approved data formats and reviewed adapters. Safetensors is designed for tensor serialization without pickle-style code execution, but does not establish model quality or repository trust. [Safetensors](https://huggingface.co/docs/safetensors/index)
- Do not enable arbitrary remote repository code as a side effect of clicking Install.
- Keep recipes declarative; dynamic third-party extensions need a separate future trust model.
- Check package sources, binary identities, archive extraction paths, and model revisions.
- Keep tokens out of source, logs, job metadata, and process arguments.
- Treat model cards and filenames as untrusted text; sanitize rich rendering and links.
- Run ordinary jobs without elevation. Drivers are an external prerequisite, with diagnostics and a link to the official vendor path when needed.
- Allow diagnostics export with redaction; prompts and source paths can be private even when tokens are absent.

A separate process isolates crashes and dependencies; it is not a security sandbox. Installing Python packages also involves trusted executable code. That is why profile provenance and reviewed integration recipes matter.

## 14. Interface architecture

The application map specifies screen-level behavior. The main navigation is **Home, Discover, Installed, Create, Enhance, Jobs, Library, Tools, Settings**. Diagnostics is reachable from Settings and from a failed operation.

Common elements include a persistent job indicator, worker/environment status, and a detail panel with task-specific controls. Creative controls appear first. Advanced settings disclose actual values and configuration only when requested. An absent dependency leads to an install action with a concrete plan; an unsupported action is explained rather than routed into a failing job.

Use WinUI semantic controls, keyboard navigation, focus management, screen-reader labels, scalable layout, and theme resources. Dark mode is the initial theme, with system/light support feasible through the same resources. Status meaning must remain understandable without color. Progress is retained when switching pages.

### Installation UX

Model details → installation plan → active install stages → ready state → create with this model. Show download/storage totals and required account access before starting. The first installation can include a runtime; later models can reuse it. A failed verification shows cached files separately from an unavailable execution environment.

### Expert reach

Expose effective parameters, recipe identity, pipeline/adapter, device policy, file locations, logs, and reproducible request export. Start with bounded settings from the recipe schema. Arbitrary command strings do not belong in the normal UI. Advanced users can run the documented worker interface separately.

## 15. Distribution and updating

**Decision:** prototype with an unpackaged development executable; evaluate a signed installer once data placement and runtime bootstrap are working. Keep writable caches/environments outside the application installation directory. A self-contained GUI can include its .NET/Windows App SDK dependencies; that does not also package Python, PyTorch, CUDA wheel dependencies, and model weights. [Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview)

Separate update channels: application, recipe catalog, runtime profiles, native tools, and model revisions. Each has a version and rollback policy. Download a new environment/tool version alongside the old one, verify it, then change the active reference. Do not mutate the active environment during a job.

Release work includes signing, installer repair/uninstall behavior, start menu registration, clean-machine bootstrap, offline behavior, and the exact third-party notices. The scaffold is source code and development instructions, not a production installer.

## 16. Verification strategy and acceptance criteria

### Contracts and orchestration

Test a real C# child-process round trip with Python, including Unicode, progress, invalid requests, cancellation, and worker failure. Assert one terminal event, no protocol data on stderr, and no diagnostic noise on stdout. Exercise a failed handshake and a worker that exits unexpectedly. Test legal/illegal job transitions and path boundaries.

### Installation

Use a tiny fixture repository before real models. Interrupt download and install stages, resume safely, test missing files, reject an unapproved recipe, and verify readiness only after completion. Test drive exhaustion with controlled fixtures. An environment fingerprint change must not reuse a stale ready state.

### GPU and artifacts

On target hardware, install the locked profile, run the actual recipe, inspect the image, capture peak memory and timings, and retry after OOM. Validate artifact files and metadata before adding them to the library. Repeat on a clean Windows user/machine before claiming automated setup works.

### Native tools and packaging

Probe an approved executable, process a small media fixture, validate output with ffprobe, cancel it, and verify the owned process exits. Validate extraction and cleanup paths. Test installer upgrade and uninstall while preserving user outputs.

### Initial product acceptance

A clean supported Windows installation can launch the GUI, choose storage, browse public models, install one verified recipe without a terminal, generate an image, cancel a job, restart after interruption, and retrieve the output with metadata. Every failure has an understandable status and a useful next action. Video acceptance is a separate milestone with measured storage and GPU requirements.

## 17. Delivery sequence

| Milestone | Concrete outcome | Exit criterion |
|---|---|---|
| M0 — architecture and scaffold | Documents, map, shell, protocol, offline checks | Boundaries are runnable and limitations explicit |
| M1 — discovery and storage | Live Hub search, detail view, selected data root, authentication boundary | Search works without inference packages; gated states handled |
| M2 — first installation recipe | Locked environment and pinned SD1.5 model | Reproducible install and real target-GPU smoke test |
| M3 — creation and history | Prompt-to-image, durable jobs, provenance and gallery | Restart, failure, cancellation, and output checks pass |
| M4 — image operations | Image-to-image/inpainting and one enhancement adapter | Each operation has an independently tested recipe |
| M5 — video | One verified video recipe and export chain | Measured feasibility on chosen hardware; honest fit/status |
| M6 — distribution | Signed installer and managed update paths | Clean-machine install/repair/update/uninstall verified |

No fixed delivery dates are invented here. The highest uncertainty is reproducible GPU inference on the 1660 Ti and the feasibility of the desired video workloads. Resolve those before investing heavily in a broad model catalog or elaborate editing UI.

## 18. Decisions recorded

| ID | Decision | Revisit when |
|---|---|---|
| ADR-001 | C# / WinUI 3 desktop shell | Cross-platform becomes a requirement |
| ADR-002 | Python out of process | A measured transport bottleneck exists |
| ADR-003 | JSON lines over owned child stdio | Remote/multiple independent clients are required |
| ADR-004 | Approved declarative recipes | An extension ecosystem is deliberately introduced |
| ADR-005 | HF cache reused; metadata ownership separate | Existing cache behavior demonstrably cannot meet needs |
| ADR-006 | One GPU lease across all engines | Multi-device scheduling is implemented |
| ADR-007 | Versioned runtime environments and side-by-side updates | Disk pressure requires a measured alternative |
| ADR-008 | Local SQLite with C# as sole writer | Remote/multiuser coordination is required |
| ADR-009 | Native components added behind adapters | A task is better handled through an existing package |
| ADR-010 | First real recipe must be benchmarked on target hardware | Hardware or execution profile changes |

## 19. Open decisions and evidence still needed

The product name, final storage root, distribution model, and desired creative styles remain user choices. Engineering must select and lock a tested PyTorch/Diffusers stack, record a pinned model revision, validate authentication storage, implement durable installation/job state, and benchmark actual image/video recipes. Native tools need approved binary sources, version/hash records, and license notices. No claim of arbitrary-model compatibility is made.

See **02-Application-Map.md** for screens, service boundaries, flows, and implementation traceability. See **03-Scaffold-Guide.md** for the runnable subset and its measured validation status.
