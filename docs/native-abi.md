# Native core ABI 1

The Windows shell uses C# 14 / .NET 10. Native internals are implemented in Rust 2024 edition and exported as a C ABI. The canonical declarations are in `native/include/media_workbench.h`.

The current core implements installation planning, Windows-aware lexical validation for relative artifact paths, streaming SHA-256 file hashing, complete verified artifact export (job record parsing, filesystem containment, cancellation, streamed copying/hashing, and atomic destination replacement), storage inventory, pinned-file verification, the one-pass copy/hash used when adding a model the user already downloaded, drive information (free space and SSD/hard disk/network type), and the automatic, bounded clean-up of application-made clutter in the data folder. Python workers own public Hub discovery, runtime provisioning, and isolated inference-library integration. C# owns the UI, presentation state, worker coordination, and Windows integration.

## Ownership and errors

- UTF-8 strings have explicit byte lengths and no terminator.
- Callers own every input/output buffer; Rust does not return heap pointers.
- `mw_abi_version()` must equal 1 before other services are used.
- Status 0 is success, 1 invalid input, 2 insufficient output capacity, 3 file I/O failure, 4 a contained internal panic, 5 cancellation, 6 checksum mismatch, and 7 a busy data folder (another owner holds its lock; only automatic clean-up returns it).
- JSON planning and hashing report their required byte count through `size_t *required`.
- Planning supports a size-query call with null output and zero capacity.
- SHA-256 output is always 64 ASCII hex bytes; querying this size does not open the file.
- Callers must provide valid nonoverlapping memory for all pointer/length pairs. C ABI boundaries cannot validate arbitrary foreign pointers.
- Panic handling contains Rust unwinds; it does not turn memory corruption or foreign pointer misuse into safe behavior.

`NativeCore.cs` uses source-generated `LibraryImport` bindings and checks ABI, output bounds, and returned status. An explicit resolver loads the core from the executable's directory without falling back to the working directory or PATH. Rust test cases and cross-language C# checks cover the actual library. The current ABI is stateless and retains no owned handles, so no SafeHandle wrapper is needed; any future owned handle must define one.

## Path policy

Relative paths use `/`. Absolute paths, drive prefixes, `..`, empty components, backslashes, NUL/control characters, invalid Windows filename characters, trailing dots/spaces, and ordinary reserved device names are rejected. This is lexical validation, not filesystem containment. Future extraction and cleanup adapters must also resolve owned roots and reject links/reparse-point escapes. Do not treat this validator alone as an archive sandbox.

## Installation planning

The recipe shape follows schema 1. The core blocks unsupported schemas/providers, draft recipes, missing immutable revisions, and missing runtime profiles. Pinned experimental recipes may run installation and local verification; `canExecute` authorizes that workflow rather than certifying an installed model. Only successful file/runtime/inference checks can produce a ready installation record. Discovery is not an installation compatibility claim.

## Verified artifact export

`mw_export_artifact` accepts bounded UTF-8 JSON with `dataRoot`, `jobId`, and `destination`. Rust reads the completed job record, verifies its identity/status/checksum declaration, resolves its image inside that job's artifact folder (including re-rooting legacy absolute records), rejects managed links/reparse points, and requires a PNG destination outside the resolved data folder. It creates an exclusive temporary file on the destination volume, streams SHA-256 while copying, syncs the file, and atomically replaces the destination only when the recorded checksum matches and cancellation has not been requested. Failed/cancelled exports preserve the destination and remove their temporary file.

The output is 64 lowercase ASCII hash bytes. A null/short output buffer returns status 2 and `required=64` before any filesystem work; querying a mutation's buffer size must never execute it. The optional synchronous `mw_export_progress` callback receives byte counts and a caller-owned context. Returning nonzero cancels before commit. Callbacks must contain exceptions and remain valid until the native call returns; Rust retains no pointers. Status 5 reports cancellation, 6 a changed image/checksum mismatch.

The GUI calls this native service directly through `NativeCore.cs` on a background thread, with a pinned callback context and a cancellation token. No Python interpreter is needed for desktop export. `artifact_export.py` is a thin ctypes adapter for worker clients and friendly diagnostics; its preflight cannot replace Rust's own checks. Real C# and Python boundary tests cover the mutation and buffer-size/cancellation contracts.

## Storage inventory

`mw_storage_summary` accepts bounded UTF-8 JSON with `dataRoot`, `runtimeProfileId`, and distinct `modelIds`. The desktop and worker adapter read the supported IDs from `profiles/storage-layout.json`; tool clients cannot supply their own layout. Rust validates each ID as a single safe relative-path component, validates the absolute non-drive-root data folder, and creates it if missing. It measures the configured runtime/models, models the user added (`models/imported`, reported separately and not counted as the installation), download caches, images, logs, job records, and Library index, preserving the existing storage response shape.

Root/managed-area ancestors must not contain links or reparse points. Links/reparse points inside each tree are skipped. Missing files/directories during traversal are tolerated; other access/I/O failures return an error with no partial snapshot. Byte counts are logical file sizes, rather than allocated/compressed sizes; hard-linked entries are counted individually. Windows available/total space comes from [GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw), including the caller's disk quotas.

A null output or capacity below 8192 returns status 2 with `required=8192`, a fixed upper bound, before any filesystem work or callback. A successful call reports the actual JSON length. The optional synchronous `mw_should_cancel` callback is polled between directories and entries; nonzero returns status 5. No callback/context is retained and foreign exceptions must be contained. The GUI/MCP/CLI service calls this from a background task with a managed cancellation token and no Python process. Worker clients use a thin ctypes adapter to the same implementation. Confirmed removal still runs under the existing worker's exclusive data-folder lock and invalidates readiness before deleting installation files.

## Pinned-file verification

`mw_verify_file` accepts bounded UTF-8 JSON with `dataRoot`, `relativePath`, unsigned byte `size`, and lowercase 64-digit `sha256`. The root must exist and be an absolute non-drive-root directory. The core validates the safe relative path, refuses links/reparse-point ancestors, checks containment and regular-file size, streams SHA-256 in 64 KiB blocks, and checks unchanged metadata and path before returning the verified hash. On Windows, [read-only file sharing](https://doc.rust-lang.org/std/os/windows/fs/trait.OpenOptionsExt.html#tymethod.share_mode) refuses existing writers and prevents new write/delete access while the file is open.

A null/short output buffer returns status 2 and `required=64` without reading input, touching files, or invoking callbacks. The synchronous optional progress callback reports bytes initially and after each block; returning nonzero cancels with status 5. Status 6 reports a size/hash mismatch or a file changed during reading. Errors leave the output buffer untouched. No state or file is written and no pointer is retained; callbacks must contain foreign exceptions.

The setup worker's `file_verification.py` adapter forwards the pinned manifest entry, throttles visible byte progress, and contains callback exceptions. The coordinator keeps its pre/post size/mtime stamps and still requires the separate runtime and GPU inference checks before committing ready state. C# provides a matching binding with cancellation and exception containment; published-app self-tests call the shipped verifier. This moves the existing file-proof work into Rust without changing the inference profile or existing readiness-stamp format.

## Model import copy

`mw_import_file` accepts bounded UTF-8 JSON with an absolute `source` file, an existing absolute non-drive-root `dataRoot`, and a safe `relativePath` for the copy. It refuses links/reparse points in the destination's ancestors, creates its parent folders, checks they resolve inside the data folder, and refuses an existing destination, so a verified file is never replaced. The source is opened with read-only sharing on Windows, which refuses a source another program is writing and blocks writes/deletion during the copy. Rust streams it once in 1 MiB blocks into `<relativePath>.partial`, hashing as it copies, checks that the source's size and modification time did not change (status 6 otherwise), syncs the copy, and renames it into place. Cancellation (status 5) or any failure removes the partial copy. The output is the copy's 64-byte SHA-256; the source is never modified.

Buffer-size queries behave as for verification: a null/short output returns status 2 and `required=64` without filesystem work or callbacks. The optional synchronous progress callback receives byte counts before the first block and after each block; nonzero cancels. The worker's `model_library.py` calls it through the same ctypes loading as the verifier, then verifies each copy with `mw_verify_file` before the GPU test. Copying never decides readiness; identification, verification, and inference checks remain required.

## Drive information

`mw_drive_info` accepts `{dataRoot}` (absolute, not a drive root; it need not exist) and returns `{freeBytes, totalBytes, driveKind}` for the nearest existing folder without creating anything. Free space comes from [GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw) and includes quotas. The kind resolves the folder's volume through its mount point and volume GUID (so mounted folders work), then asks the storage stack with `IOCTL_STORAGE_QUERY_PROPERTY` on a handle opened with no access rights (no administrator rights needed): a USB bus reports `usb`, a seek penalty `hdd`, none `ssd`. Network and removable drives are reported from the drive type; volumes that do not answer, such as some RAID or Storage Spaces volumes, report `unknown`. The vocabulary matches the worker's hardware profile. A null/short output returns status 2 with `required=256` and no filesystem work. Storage inventory reports the same `driveKind`. Desktop Create, Tools, and Settings use it for the low-space (below 5 GiB) and slower-drive notices.

## Automatic clean-up

`mw_tidy_data_folder` accepts `{dataRoot, runtimeProfileId, modelIds, installationVerified, removeDownloadCaches}`; `removeDownloadCaches` requires `installationVerified` (status 1 otherwise), and `imported` is not a valid model ID. A missing data folder is not created and returns an empty report. Every removal happens while Rust holds the data-folder lock, an exclusive `LockFileEx` on byte 0 of `.operation.lock`, the same byte range the Python worker locks for setup, generation, model import, and removal; while another owner holds it the call returns status 7 without changes. Rules, all in this one place:

- **Unfinished-job records.** Records named `<32 hex>.json` with a readable matching `id`, ordered newest first like Library (modification time, then name). The newest 50 failed, cancelled, or interrupted records are kept with their private inputs and logs; only a crash's `image.partial` is removed from them. Older ones lose their private input copies (`source.*`, `mask.png`) and half-written image unless the folder has a finished `image.png`, then their own log, and the record itself last, so an interruption never orphans files. Running, succeeded, damaged, setup, and unknown records are never removed.
- **Logs.** Regular `*.log` files directly in `logs/`, newest first: beyond the newest 300 or older than 30 days are removed, except logs named for a running or kept unfinished job. Other files and folders there are kept.
- **Unfinished downloads.** `.partial` (Media Workbench) and `.incomplete` (Hugging Face) files in `bootstrap/` and `models/<modelId>/`: older than 72 hours, or all of them once `installationVerified`. Models the user added are not touched (their own recovery removes failed imports).
- **Download caches.** With `removeDownloadCaches`, `cache/` and `bootstrap/` are renamed into `.removing/` and deleted from there; every call first finishes any `.removing/` left by a crash or cancellation, so a half-deleted cache is never used.

Links and junctions are never followed: managed-area ancestors containing one are refused (status 1), links inside trees are skipped or, inside a cache being removed, deleted as links. Read-only cache files are made writable and deleted. A file another program holds open is counted as `skipped` and tried again next time. The report is `{removedFiles, removedBytes, logs, jobRecords, partialDownloads, downloadCaches, skipped, policy}`, where `policy` states the limits (`logs` 300, `logDays` 30, `problemJobs` 50, `partialHours` 72) so clients can check they match Library. A null/short output returns status 2 with `required=1024` and no filesystem work; the optional cancellation callback is polled between entries (status 5). The desktop calls it directly at startup (removing caches only when setup is verified and Settings does not keep them); the worker calls it through the storage adapter after a verified setup.

## Future additions


Move bootstrap/package verification and owned-storage operations into this core as they are needed. Keep new exports narrow and append-only within ABI 1, or bump the ABI when compatibility breaks. Native exports should have matching header documentation and meaningful Rust plus C# boundary checks.
