# Changelog

## 0.2.0-alpha.1 — 2026-10-17

- First SD 1.5 setup and prompt-to-image pipeline, with a private Python 3.12/CUDA runtime, exact package lock, pinned Hub commit, and selected safetensors files.
- Native plan gate, storage preflight, model checksum verification, and a real local GPU inference test before ready state.
- Persistent setup attempts, interruption detection, retained downloads on retry, streamed progress, and owned-child cancellation.
- Create controls for prompt/negative prompt, seed, and inference steps; PNG preview and saved settings/device/output evidence.
- Library reads recent successful image jobs; automatic model status/library refresh when opening those pages.
- Additional setup/recovery/cancellation checks and published-payload requirements for inference files.
- Each worker runs in a kill-on-close Windows Job Object, so a forced stop, worker failure, or app crash also ends the inference child and other descendants.
- Generation jobs left running by a stopped owner are recorded as interrupted, partial images and unfinished records are removed, damaged records no longer block Library, and Library reports recovered jobs.

Experimental Windows x64/NVIDIA profile, 6 GB VRAM minimum, 512 × 512 FP32 offload. Protected Hub access, arbitrary model installation, image editing, and video remain planned. Installer unsigned; clean Windows 10 verification pending.

## 0.1.0-alpha.1 — 2026-10-17

First installable development alpha of Media Workbench.

- Dark WinUI 3 MVVM shell using C# 14 and .NET 10.
- Rust native core behind a documented C ABI: installation planning, Windows relative-path policy, and streaming SHA-256.
- Bundled CPython discovery worker, live public Hugging Face search, task filters, and model details.
- Storage settings, component diagnostics, operation cancellation, and session activity.
- Per-user Windows installer with Start menu integration, custom icon, stable taskbar identity, and single-instance launch.
- Source architecture documents, native/worker boundary checks, CI, and packaged resource/runtime smoke tests.

Model installation, inference environment provisioning, image/video generation, account access, and durable media history remain in development. This alpha downloads no model weights. The installer is unsigned; Windows 10 clean-machine verification is pending.
