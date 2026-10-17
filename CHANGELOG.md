# Changelog

## 0.1.0-alpha.1 — 2026-10-17

First installable development alpha of Media Workbench.

- Dark WinUI 3 MVVM shell using C# 14 and .NET 10.
- Rust native core behind a documented C ABI: installation planning, Windows relative-path policy, and streaming SHA-256.
- Bundled CPython discovery worker, live public Hugging Face search, task filters, and model details.
- Storage settings, component diagnostics, operation cancellation, and session activity.
- Per-user Windows installer with Start menu integration, custom icon, stable taskbar identity, and single-instance launch.
- Source architecture documents, native/worker boundary checks, CI, and packaged resource/runtime smoke tests.

Model installation, inference environment provisioning, image/video generation, account access, and durable media history remain in development. This alpha downloads no model weights. The installer is unsigned; Windows 10 clean-machine verification is pending.
