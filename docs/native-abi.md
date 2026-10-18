# Native core ABI 1

The Windows shell uses C# 14 / .NET 10. Native internals are implemented in Rust 2024 edition and exported as a C ABI. The canonical declarations are in `native/include/media_workbench.h`.

The current core implements installation planning, Windows-aware lexical validation for relative artifact paths, streaming SHA-256 file hashing, complete verified artifact export (job record parsing, filesystem containment, cancellation, streamed copying/hashing, and atomic destination replacement), and storage inventory. Python workers own public Hub discovery, runtime provisioning, and isolated inference-library integration. C# owns the UI, presentation state, worker coordination, and Windows integration.

## Ownership and errors

- UTF-8 strings have explicit byte lengths and no terminator.
- Callers own every input/output buffer; Rust does not return heap pointers.
- `mw_abi_version()` must equal 1 before other services are used.
- Status 0 is success, 1 invalid input, 2 insufficient output capacity, 3 file I/O failure, 4 a contained internal panic, 5 cancellation, and 6 checksum mismatch.
- JSON planning and hashing report their required byte count through `size_t *required`.
- Planning supports a size-query call with null output and zero capacity.
- SHA-256 output is always 64 ASCII hex bytes; querying this size does not open the file.
- Callers must provide valid nonoverlapping memory for all pointer/length pairs. C ABI boundaries cannot validate arbitrary foreign pointers.
- Panic handling contains Rust unwinds; it does not turn memory corruption or foreign pointer misuse into safe behavior.

`NativeCore.cs` uses source-generated `LibraryImport` bindings and checks ABI, output bounds, and returned status. Rust test cases and cross-language C# checks cover the actual library.

## Path policy

Relative paths use `/`. Absolute paths, drive prefixes, `..`, empty components, backslashes, NUL/control characters, invalid Windows filename characters, trailing dots/spaces, and ordinary reserved device names are rejected. This is lexical validation, not filesystem containment. Future extraction and cleanup adapters must also resolve owned roots and reject links/reparse-point escapes. Do not treat this validator alone as an archive sandbox.

## Installation planning

The recipe shape follows schema 1. The core blocks unsupported schemas/providers, draft recipes, missing immutable revisions, and missing runtime profiles. Pinned experimental recipes may run installation and local verification; `canExecute` authorizes that workflow rather than certifying an installed model. Only successful file/runtime/inference checks can produce a ready installation record. Discovery is not an installation compatibility claim.

## Verified artifact export

`mw_export_artifact` accepts bounded UTF-8 JSON with `dataRoot`, `jobId`, and `destination`. Rust reads the completed job record, verifies its identity/status/checksum declaration, resolves its image inside that job's artifact folder (including re-rooting legacy absolute records), rejects managed links/reparse points, and requires a PNG destination outside the resolved data folder. It creates an exclusive temporary file on the destination volume, streams SHA-256 while copying, syncs the file, and atomically replaces the destination only when the recorded checksum matches and cancellation has not been requested. Failed/cancelled exports preserve the destination and remove their temporary file.

The output is 64 lowercase ASCII hash bytes. A null/short output buffer returns status 2 and `required=64` before any filesystem work; querying a mutation's buffer size must never execute it. The optional synchronous `mw_export_progress` callback receives byte counts and a caller-owned context. Returning nonzero cancels before commit. Callbacks must contain exceptions and remain valid until the native call returns; Rust retains no pointers. Status 5 reports cancellation, 6 a changed image/checksum mismatch.

The GUI calls this native service directly through `NativeCore.cs` on a background thread, with a pinned callback context and a cancellation token. No Python interpreter is needed for desktop export. `artifact_export.py` is a thin ctypes adapter for worker clients and friendly diagnostics; its preflight cannot replace Rust's own checks. Real C# and Python boundary tests cover the mutation and buffer-size/cancellation contracts.

## Storage inventory

`mw_storage_summary` accepts bounded UTF-8 JSON with `dataRoot`, `runtimeProfileId`, and distinct `modelIds`. The desktop and worker adapter read the supported IDs from `profiles/storage-layout.json`; tool clients cannot supply their own layout. Rust validates each ID as a single safe relative-path component, validates the absolute non-drive-root data folder, and creates it if missing. It measures the configured runtime/models, download caches, images, logs, job records, and Library index, preserving the existing storage response shape.

Root/managed-area ancestors must not contain links or reparse points. Links/reparse points inside each tree are skipped. Missing files/directories during traversal are tolerated; other access/I/O failures return an error with no partial snapshot. Byte counts are logical file sizes, rather than allocated/compressed sizes; hard-linked entries are counted individually. Windows available/total space comes from [GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw), including the caller's disk quotas.

A null output or capacity below 8192 returns status 2 with `required=8192`, a fixed upper bound, before any filesystem work or callback. A successful call reports the actual JSON length. The optional synchronous `mw_should_cancel` callback is polled between directories and entries; nonzero returns status 5. No callback/context is retained and foreign exceptions must be contained. The GUI/MCP/CLI service calls this from a background task with a managed cancellation token and no Python process. Worker clients use a thin ctypes adapter to the same implementation. Confirmed removal still runs under the existing worker's exclusive data-folder lock and invalidates readiness before deleting installation files.

## Pinned-file verification

`mw_verify_file` accepts bounded UTF-8 JSON with `dataRoot`, `relativePath`, unsigned byte `size`, and lowercase 64-digit `sha256`. The root must exist and be an absolute non-drive-root directory. The core validates the safe relative path, refuses links/reparse-point ancestors, checks containment and regular-file size, streams SHA-256 in 64 KiB blocks, and checks unchanged metadata and path before returning the verified hash. On Windows, [read-only file sharing](https://doc.rust-lang.org/std/os/windows/fs/trait.OpenOptionsExt.html#tymethod.share_mode) refuses existing writers and prevents new write/delete access while the file is open.

A null/short output buffer returns status 2 and `required=64` without reading input, touching files, or invoking callbacks. The synchronous optional progress callback reports bytes initially and after each block; returning nonzero cancels with status 5. Status 6 reports a size/hash mismatch or a file changed during reading. Errors leave the output buffer untouched. No state or file is written and no pointer is retained; callbacks must contain foreign exceptions.

The setup worker's `file_verification.py` adapter forwards the pinned manifest entry, throttles visible byte progress, and contains callback exceptions. The coordinator keeps its pre/post size/mtime stamps and still requires the separate runtime and GPU inference checks before committing ready state. C# provides a matching binding with cancellation and exception containment; published-app self-tests call the shipped verifier. This moves the existing file-proof work into Rust without changing the inference profile or existing readiness-stamp format.

## Future additions


Move bootstrap/package verification and owned-storage operations into this core as they are needed. Keep new exports narrow and append-only within ABI 1, or bump the ABI when compatibility breaks. Native exports should have matching header documentation and meaningful Rust plus C# boundary checks.
