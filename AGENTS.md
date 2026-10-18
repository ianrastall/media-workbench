# Media Workbench development

- Target Windows 10 22H2 / Windows 11 x64, C# 14, .NET 10, WinUI 3, and MVVM.
- The native core is Rust, exposed through the versioned C ABI in `native/include/media_workbench.h`.
- Keep Python workers out of process. Do not import AI runtimes into the GUI process.
- Keep secrets, downloaded models/runtimes, local settings, logs, and generated artifacts out of Git.
- Never label a model installed or verified until its files, runtime, and inference checks succeed.
- Run `scripts/check.ps1` for relevant Rust, Python, and C# checks; `scripts/build.ps1` builds the desktop application.
- Update README and docs/current-status.md to describe actual implemented behavior.
- The application is MIT-licensed (`LICENSE`); preserve upstream notices for third-party components and models.

## Product principles

Apply these to every feature, in this order when they conflict:

1. **Easy to use.** Plain language and good defaults on the main path; technical controls go under Advanced options with plain labels. A beginner should never need to know about runtimes, precision, or samplers.
2. **Stable.** Never trade reliability or verification gates for a feature.
3. **Aware of the computer it runs on.** Detect the GPU and its memory, system memory, and the data folder's free space and drive type, and choose settings from them (precision, memory offloading, tile sizes, what can run at all) instead of one fixed profile for everyone. Say in plain words when the computer limits something, and how to improve it. Any automatic choice that changes how inference runs must still pass the real GPU verification for that choice.
4. **Clean.** Do not leave clutter. Remove intermediate and temporary files (partial downloads, hand-over files, interrupted work, old update installers) as soon as they are no longer needed, including after a crash. Keep logs and caches bounded, and tell the user what is kept and why. Never delete images, job records, a user's own files, or anything a retry or **Use settings** still needs.
5. **Mindful of drive space.** Check free space before any operation that writes a lot, warn before space runs low rather than failing midway, and say when a full or slow drive is the reason something is slow.

## Git and releases

- Commit and push directly to `main` without asking. Do not create feature branches or pull requests unless explicitly requested.
- Commit and push automatically when a coherent piece of work passes `scripts/check.ps1` and builds; never push failing checks.
- Releases are produced by GitHub Actions, never from a developer machine. To release, change `<Version>` in `Directory.Build.props` and add a matching `## <version> — <date>` section to `CHANGELOG.md` in the same push. On `main`, `.github/workflows/build.yml` builds, checks, packages the installer, and publishes `v<version>` with that changelog section when the tag does not yet exist. Versions containing `-` are published as pre-releases.
- Keep the repository machine-independent: no personal paths, local hardware assumptions, credentials, or locally built binaries in commits. Validation notes may record the hardware a result was observed on.
