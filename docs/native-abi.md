# Native core ABI 1

The Windows shell uses C# 14 / .NET 10. Native internals are implemented in Rust 2024 edition and exported as a C ABI. The canonical declarations are in `native/include/media_workbench.h`.

The current core implements installation planning, Windows-aware lexical validation for relative artifact paths, and streaming SHA-256 file hashing. Python workers own public Hub discovery and will own inference-library integration. C# owns the UI, presentation state, worker coordination, and Windows integration.

## Ownership and errors

- UTF-8 strings have explicit byte lengths and no terminator.
- Callers own every input/output buffer; Rust does not return heap pointers.
- `mw_abi_version()` must equal 1 before other services are used.
- Status 0 is success, 1 invalid input, 2 insufficient output capacity, 3 file I/O failure, 4 a contained internal panic.
- JSON planning and hashing report their required byte count through `size_t *required`.
- Planning supports a size-query call with null output and zero capacity.
- SHA-256 output is always 64 ASCII hex bytes; querying this size does not open the file.
- Callers must provide valid nonoverlapping memory for all pointer/length pairs. C ABI boundaries cannot validate arbitrary foreign pointers.
- Panic handling contains Rust unwinds; it does not turn memory corruption or foreign pointer misuse into safe behavior.

`NativeCore.cs` uses source-generated `LibraryImport` bindings and checks ABI, output bounds, and returned status. Rust test cases and cross-language C# checks cover the actual library.

## Path policy

Relative paths use `/`. Absolute paths, drive prefixes, `..`, empty components, backslashes, NUL/control characters, invalid Windows filename characters, trailing dots/spaces, and ordinary reserved device names are rejected. This is lexical validation, not filesystem containment. Future extraction and cleanup adapters must also resolve owned roots and reject links/reparse-point escapes. Do not treat this validator alone as an archive sandbox.

## Installation planning

The recipe shape currently follows schema 1. The core reports missing pinned model revisions, unverified recipes, and missing runtime profiles. All plans remain nonexecutable until a real installer and target-hardware verification exist. Discovery is not an installation compatibility claim.

## Future additions

Move performance-sensitive file verification and owned-storage operations into this core as they are needed. Keep new exports narrow and append-only within ABI 1, or bump the ABI when compatibility breaks. Native exports should have matching header documentation and meaningful Rust plus C# boundary checks.
