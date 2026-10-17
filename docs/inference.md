# First inference pipeline

The desktop app coordinates one SD 1.5 recipe. Search results never become executable code or automatically installable models. The recipe is experimental; each machine must pass inference verification before it is ready.

## Runtime boundary

```mermaid
sequenceDiagram
    participant UI as WinUI / MVVM
    participant Core as C# service + Rust plan
    participant Setup as Discovery Python 3.14 child
    participant ML as Private Python 3.12 child
    participant Disk as Selected data folder
    UI->>Core: Install / verify
    Core->>Core: Validate pinned recipe through C ABI
    Core->>Setup: installation.run (JSON lines)
    Setup->>Disk: Acquire lock; save attempt
    Setup->>Setup: Verify Python/uv archives; sync locked wheels
    Setup->>ML: Download selected files using Hub SDK
    ML-->>Setup: Progress
    Setup->>Disk: Check all model sizes and SHA-256
    Setup->>ML: Verify real 512 × 512 CUDA inference
    ML-->>Setup: GPU, precision, time, memory evidence
    Setup->>Disk: Atomic ready record
    Setup-->>UI: Terminal result
```

The bundled discovery interpreter remains independent of the ML runtime. Official CPython 3.12.10 embeddable x64 is extracted under `runtimes/sd15-cu126-v1/python`. Its isolated path file adds only its private site-packages directory. A checksum-pinned uv executable installs wheel-only packages into that directory. No system Python, PATH, global pip environment, or CUDA toolkit is modified.

The ML child uses `-I -u` and a closed stdin rather than inheriting the coordinator's protocol input. Python user-site packages and inherited Python configuration are excluded. Child output/diagnostics are saved under the data folder's `logs/` directory; only parsed progress and terminal events reach the GUI protocol. CPython 3.12.10 is the final official 3.12 maintenance binary; moving to a newer security-patched Windows runtime is a separate profile update, not an automatic untested interpreter replacement.

`profiles/sd15-cu126.in` records direct dependencies; `.lock` records exact transitive versions and hashes. PyTorch's CUDA 12.6 wheel is an immutable URL with a pinned SHA-256. uv is invoked with hash enforcement, no configuration discovery, an explicit interpreter, an owned cache, and no source builds. Inherited pip/uv configuration variables are excluded. See [uv hash checking](https://docs.astral.sh/uv/reference/settings/#require-hashes) and [official PyTorch builds](https://pytorch.org/get-started/previous-versions/).

`profiles/sd15-files.json` selects 16 files, approximately 2.55 GiB, from model commit `451f4fe16113bff5a5d2269ed5ad43b0592e9a14`. It excludes pickle checkpoints and duplicate full-precision weights. The supported Hub SDK handles selected-file downloads and its local cache. Each selected file has a checked size and SHA-256 before inference. Public requests explicitly disable implicit account tokens. Review the [model card and license](https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-v1-5).

## Execution and evidence

The inference child uses the built-in `StableDiffusionPipeline`, safetensors, local files only, and offline environment flags. Model files cannot select custom remote Python code. The safety checker remains enabled. Stored fp16 weights are loaded into FP32 computation, with model CPU offload and VAE slicing. This is the conservative initial GTX 16-series profile; there is no automatic precision tuning or arbitrary pipeline adapter yet. See [Diffusers memory management](https://huggingface.co/docs/diffusers/en/optimization/memory).

Before taking the data-folder lock, the coordinator passes the recipe through the Rust installation gate (`mw_plan_installation`, loaded with `ctypes` from the app's `media_workbench_core.dll`), so a blocked recipe never starts an attempt whichever client asked. A fresh setup requires 20 GiB free; once the runtime is recorded as synced against the current lock, re-verification requires only the size of missing model files plus 2 GiB. Setup requires free space, NVIDIA device detection, CUDA availability, and the recipe's `minimumVramMiB` (6144). Setup (via nvidia-smi) and the inference child (via CUDA) apply that same limit, with a 128 MiB allowance because drivers report slightly less than nominal capacity, to the same device: the child runs with `CUDA_DEVICE_ORDER=PCI_BUS_ID`, so its `cuda:0` is the first `CUDA_VISIBLE_DEVICES` entry (or nvidia-smi index 0) that setup inspected. A missing or unreadable memory value such as `[N/A]` fails setup with an explanation. Verification renders a fixed 512 × 512 prompt with four inference steps, checks output dimensions and image variation, and rejects safety-filtered output. The ready record includes GPU name, Torch/CUDA versions, precision, inference duration, peak allocated GPU memory, UTC timestamp, the size and modification time of each verified model file, and a readiness stamp. The stamp covers an explicit `ADAPTER_VERSION` in `inference_setup.py` plus the profile, lock, file manifest, and recipe with line endings normalized; editing worker source does not revoke readiness, so changes that alter what verification means must bump the adapter version. This is a smoke test, not a performance certification or guarantee for every prompt or driver.

Generation accepts guidance from 1 to 20, a size of 512 × 512, 512 × 768, or 768 × 512, and a sampler: `pndm` (the model's own scheduler), `dpmpp-2m` (DPM++ 2M with Karras sigmas), or `euler-a` (Euler ancestral); the coordinator rejects anything else before touching storage. Generation checks the matching readiness stamp and compares each model file's size and modification time with the values recorded at verification (full SHA-256 runs only during install / verify), validates inputs, starts an isolated inference child, and forwards per-step progress. A PNG is written under a unique artifact directory and finalized by atomic rename. Only then is the job marked succeeded. The job JSON stores the image path relative to `artifacts/` (`<id>/image.png`), the prompt, negative prompt, seed, steps, size, guidance, sampler, model recipe/profile, output SHA-256, device, and timing. Seeds reproduce settings; identical pixels across different drivers/hardware are not guaranteed.

## Data and recovery

```text
<data folder>/
  .operation.lock                     OS-released exclusive lock
  bootstrap/                          verified Python/uv archives and uv executable
  runtimes/sd15-cu126-v1/python/       private interpreter and packages
  models/sd15-baseline/               selected model files and Hub download metadata
  cache/uv/                           wheel cache, separate from system caches
  jobs/install-sd15.json              latest setup attempt and readiness evidence
  jobs/<id>.json                     generation settings, status, output evidence
  artifacts/<id>/image.png            finalized generated image
  logs/<operation>-<id>.log           local child output/diagnostics (generation logs use the job ID)
```

One operation owns a data folder at a time, including across separate clients. Setup writes preparing/runtime/downloading/verifying states before each stage; ready is atomic and last. Normal failures and cancellation record their outcome. Work left in progress with no lock owner is changed to interrupted the next time the folder is idle and checked: a setup attempt at status, install, or generation; a `running` generation job at status, Library, install, or generation. Recovery removes that job's `image.partial`, unfinished atomic-write temporary records, and reports how many jobs it recorded so Library can say so. Damaged job records are skipped rather than blocking Library. A changed readiness stamp, or a model file whose size or modification time changed, invalidates readiness. Retry retains completed downloads; no rollback deletes a previous model/runtime automatically. Removal happens only when the user asks in Settings → Storage (`storage.clear`): download caches (`cache/`, `bootstrap/`), logs, or the installation (the ready record first, then runtime, model, and caches). It takes the data-folder lock, so it waits for no one and fails if another operation owns the folder; images and job records are never removed.

Cancellation reaches the child through the coordinator and kills the ML process if needed. The C# transport escalates after three seconds to terminating its owned process tree. GPU kernels/imports are not guaranteed to exit cooperatively. Each worker is placed in its own Windows Job Object with kill-on-close before it receives a request, so the ML child, uv, and any other descendant inherit it. Forced stops terminate the whole job, including a descendant whose coordinator already exited; disposing a worker closes its job; and if the app itself crashes or is killed, Windows closes the handle and ends the tree. The coordinator's lock is then released and the next check records the job as interrupted. Partial, uncommitted outputs do not enter Library. Library shows up to 200 recent successful job records and up to 50 recent failed, cancelled, or interrupted ones; failures no longer push older images out of the listing. Each generation writes its child log to `logs/generate-<id>.log`, and Library links it only by that fixed name. It is a directory scan, not a database-backed or indexed asset manager. It resolves each image path inside that job's `artifacts/<id>/` folder before listing it; records from 0.2.0-alpha.1 that stored an absolute path are re-rooted by job ID, so they remain listed after the data folder moves.

## Current limits

Windows x64/NVIDIA only; one model; three sizes (verification itself runs only 512 × 512, so the larger sizes are not tested per machine); FP32 offload. No account tokens, LoRAs, image input, video, queue, automatic cache pruning, signature-verified updates, or telemetry. Repeated generation currently reloads the model in a fresh child. Downloads and runtime caches can consume substantially more disk than the model alone. Installed package hashes are enforced on download, rather than continuously auditing every extracted package file. The worker is dependency/process isolation, not a hostile-code sandbox. Clean-machine Visual C++ runtime and Windows 10 testing remain necessary before broad support claims.
