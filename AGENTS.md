# Media Workbench development

- Target Windows 10 22H2 / Windows 11 x64, C# 14, .NET 10, WinUI 3, and MVVM.
- The native core is Rust, exposed through the versioned C ABI in `native/include/media_workbench.h`.
- Keep Python workers out of process. Do not import AI runtimes into the GUI process.
- Keep secrets, downloaded models/runtimes, local settings, logs, and generated artifacts out of Git.
- Never label a model installed or verified until its files, runtime, and inference checks succeed.
- Run `scripts/check.ps1` for relevant Rust, Python, and C# checks; `scripts/build.ps1` builds the desktop application.
- Update README and docs/current-status.md to describe actual implemented behavior.
- No application license has been selected yet; preserve upstream notices.
