#ifndef MEDIA_WORKBENCH_H
#define MEDIA_WORKBENCH_H
#include <stddef.h>
#include <stdint.h>
#ifdef _WIN32
#define MW_API __declspec(dllimport)
#else
#define MW_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* ABI 1. All strings are UTF-8 with explicit byte lengths, without a NUL.
   Buffers belong to the caller; no allocator ownership crosses this ABI.
   Status: 0 success; 1 invalid input; 2 output buffer too small; 3 I/O error;
           4 internal panic contained at the boundary; 5 cancelled; 6 checksum mismatch.
   Query output size using output=NULL, capacity=0 and a valid required pointer.
   Pointer/length pairs must refer to readable/writable memory as appropriate. */
MW_API uint32_t mw_abi_version(void);
MW_API int32_t mw_validate_relative_path(const uint8_t *input, size_t length);
MW_API int32_t mw_plan_installation(const uint8_t *input, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
MW_API int32_t mw_sha256_file(const uint8_t *path, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 export service. JSON: {dataRoot, jobId, destination}.
   Validates a succeeded job and contained image; destination must be a PNG outside dataRoot.
   Streams and verifies SHA-256, then atomically replaces the destination. On failure/cancel
   the existing destination is kept. Output is 64 lowercase SHA-256 bytes on success.
   output=NULL or capacity<64 returns 2, required=64, with NO filesystem work.
   The synchronous callback receives byte counts; return nonzero to cancel. It may be NULL.
   No pointers/callbacks are retained. Callback must not throw/unwind across the ABI. */
typedef int32_t (*mw_export_progress)(uint64_t completed, uint64_t total, void *context);
MW_API int32_t mw_export_artifact(const uint8_t *input, size_t length,
    mw_export_progress progress, void *context,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 Windows storage inventory. JSON: {dataRoot, runtimeProfileId, modelIds}.
   IDs are single safe path components; modelIds must be distinct. Creates a missing dataRoot.
   Returns JSON {freeBytes,totalBytes,installationBytes,items:[{id,label,bytes,files,clearable,note}]}.
   Refuses links/reparse points in root/managed area ancestors, skips links inside each tree,
   tolerates files disappearing during traversal, and fails on other I/O errors (no partial totals).
   output=NULL or capacity<8192 returns 2, required=8192 (a fixed upper bound), without
   filesystem work. Success sets required to the actual JSON length. No pointers are retained.
   The synchronous optional callback returns nonzero to cancel; it must not throw/unwind.
   Available disk space accounts for the calling user's quotas. Counts are logical file sizes. */
typedef int32_t (*mw_should_cancel)(void *context);
MW_API int32_t mw_storage_summary(const uint8_t *input, size_t length,
    mw_should_cancel cancel, void *context,
    uint8_t *output, size_t capacity, size_t *required);
#ifdef __cplusplus
}
#endif
#endif
