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
           4 internal panic contained at the boundary; 5 cancelled; 6 checksum mismatch;
           7 the data folder is busy (another owner holds its lock).
   Query output size using output=NULL, capacity=0 and a valid required pointer.
   Pointer/length pairs must refer to readable/writable memory as appropriate. */
MW_API uint32_t mw_abi_version(void);
/* Additive ABI 1 pure SD 1.5 tensor-layout policy. JSON: {kind,tensors:{name:{dtype,shape}}}.
   kind is checkpoint, unet, textEncoder or vae; shapes are unsigned integer arrays.
   Returns {compatible,ownVae,message}; an unsupported layout is status 0 with compatible=false
   and a plain-language reason. Bounds: 100 MiB input, 32000 tensors, rank 8, dimensions <=2^31.
   Invalid input returns 1 without changing output. Input must come from a structurally checked
   safetensors header; this does not validate offsets, read weights or grant readiness.
   No filesystem/network work. output=NULL or capacity<1024 returns 2, required=1024,
   without reading input. No pointers are retained. */
MW_API int32_t mw_check_model_compatibility(const uint8_t *input, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 public Hub download plan. JSON: {repoId,revision,license,gated,
   files:[{path,bytes,sha256}]}. Returns {files,blocker}; only complete root-level
   safetensors candidates with bounded sizes and SHA-256 are returned, smallest first.
   Requires public access, a license and immutable 40-digit revision. No filesystem/network
   work; metadata never grants model readiness. Header, file, runtime and GPU checks follow.
   output=NULL or capacity<65536 returns 2, required=65536 without reading input.
   Invalid/bounded input returns 1. No pointers are retained. */
MW_API int32_t mw_plan_hub_model(const uint8_t *input, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
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
   Returns JSON {freeBytes,totalBytes,installationBytes,items:[{id,label,bytes,files,clearable,note}],
   driveKind} (driveKind as for mw_drive_info).
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
/* Additive ABI 1 pinned-file verification. JSON: {dataRoot,relativePath,size,sha256}.
   dataRoot must exist and be an absolute non-drive-root folder. relativePath uses safe
   forward-slash components; links/reparse points in its ancestors are refused.
   sha256 is 64 lowercase hex digits; size is an unsigned byte count.
   Checks regular-file size, streamed SHA-256, and unchanged metadata/path. On Windows
   the file is opened with read sharing only, refusing concurrent writers/deletion.
   Output is the verified 64-byte hash. Status 6 means size/hash/file changed.
   Does not write files or alter model readiness; runtime/inference checks remain required.
   output=NULL or capacity<64 returns 2, required=64, without filesystem work/callbacks.
   The optional synchronous callback receives byte counts initially and every 64 KiB;
   nonzero returns 5. No pointers are retained; callbacks must not throw/unwind. */
MW_API int32_t mw_verify_file(const uint8_t *input, size_t length,
    mw_export_progress progress, void *context,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 model import copy. JSON: {source,dataRoot,relativePath}.
   source is an absolute regular file anywhere; dataRoot an existing absolute non-drive-root
   folder; relativePath safe forward-slash components whose ancestors contain no links.
   The destination must not exist. Streams the source once into "<relativePath>.partial",
   hashing as it copies, syncs, and renames it into place. On Windows the source is opened
   with read sharing only, refusing concurrent writers/deletion. Status 6 means the source
   changed while copying. Failure or cancellation (5) removes the partial copy. Output is
   the copy's 64-byte SHA-256. The source is never modified. Does not decide readiness;
   identification, verification, and GPU inference checks remain required.
   output=NULL or capacity<64 returns 2, required=64, without filesystem work/callbacks.
   The optional synchronous callback receives byte counts; nonzero cancels.
   No pointers are retained; callbacks must not throw/unwind. */
MW_API int32_t mw_import_file(const uint8_t *input, size_t length,
    mw_export_progress progress, void *context,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 drive information. JSON: {dataRoot} (absolute, not a drive root; it may not exist).
   Uses the nearest existing folder and creates nothing. Returns JSON {freeBytes,totalBytes,driveKind,
   volume} with driveKind "ssd", "hdd", "usb", "removable", "network", or "unknown" (volumes that do not
   report a seek penalty, such as some RAID or Storage Spaces volumes) and volume the mount point
   ("D:\", a mounted folder, or a share root; "" if unknown). Free space includes quotas.
   output=NULL or capacity<8192 returns 2, required=8192, without filesystem work. */
/* Additive ABI 1 data-folder move planning. JSON: {source, destination} (absolute, not drive roots;
   destination may not exist). Reads only. Returns JSON {files,bytes,freeBytes,neededBytes,sameVolume,
   leftover,blocker}: what would be copied (everything except .operation.lock, temp/, library/ and
   .removing/ at the top level), the destination drive's free space, bytes plus a 1 GiB reserve,
   whether both are on one volume, whether an interrupted move left a staging folder, and a plain
   reason the move cannot start ("" when it can: destination inside/containing the source, not
   empty, a link inside the data folder, or not enough space).
   output=NULL or capacity<4096 returns 2, required=4096, without filesystem work. */
MW_API int32_t mw_plan_data_move(const uint8_t *input, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 data-folder move. JSON: {source, destination, abandon?}. Holds the source's
   data-folder lock (status 7 while another owner holds it), refuses with 1 when planning reports a
   blocker, copies into the marked staging folder "<parent>/.<name>.moving", hashes every file while
   reading it, re-reads and compares the copy (status 6 on mismatch), keeps modification times, and
   renames the staging folder to the destination last. The source is never changed. Failure or
   cancellation (status 5) removes the staging folder. abandon=true only removes a marked staging
   folder an interrupted move left. Progress counts copy and verification bytes (total = 2 x bytes).
   Returns JSON {files,bytes,destination}.
   output=NULL or capacity<4096 returns 2, required=4096, without filesystem work or callbacks. */
MW_API int32_t mw_move_data_folder(const uint8_t *input, size_t length,
    mw_export_progress progress, void *context,
    uint8_t *output, size_t capacity, size_t *required);
MW_API int32_t mw_drive_info(const uint8_t *input, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 automatic clean-up. JSON: {dataRoot, runtimeProfileId, modelIds,
   installationVerified, removeDownloadCaches}. removeDownloadCaches requires installationVerified.
   A missing dataRoot is not created and returns an empty report. Takes the data-folder lock (byte 0
   of "<dataRoot>/.operation.lock", the range the worker locks) and returns status 7 without changes
   while another owner holds it. Under the lock it removes: failed/cancelled/interrupted job records
   beyond the newest 50 (with their logs, private input copies, and half-written image, never a
   finished image.png); logs beyond the newest 300 or older than 30 days, except logs of running or
   kept unfinished jobs; ".partial"/".incomplete" files in bootstrap/ and models/<modelId>/ older
   than 72 hours, or all of them when installationVerified; temp/work/<32 hex> worker folders whose
   ".owner" file can be deleted (a running worker holds it open) or, without one, older than 10
   minutes; and, when removeDownloadCaches, cache/
   and bootstrap/ (renamed into ".removing/" first, which the next call finishes after a crash).
   Never follows links, refuses links in managed ancestors, and skips damaged or unknown files.
   Returns JSON {removedFiles,removedBytes,logs,jobRecords,partialDownloads,scratchFolders,downloadCaches,skipped,
   policy:{logs,logDays,problemJobs,partialHours}}; files that cannot be removed now are "skipped".
   output=NULL or capacity<1024 returns 2, required=1024, without filesystem work or callbacks.
   The optional synchronous callback returns nonzero to cancel (status 5); it must not unwind. */
MW_API int32_t mw_tidy_data_folder(const uint8_t *input, size_t length,
    mw_should_cancel cancel, void *context,
    uint8_t *output, size_t capacity, size_t *required);
/* Additive ABI 1 short-clip encoding. JSON: {framesPath, width, height, frames, fps, destination}.
   framesPath (absolute) holds exactly width*height*3*frames bytes of top-down RGB24 frames;
   width and height are even, 64-4096; frames 1-240; fps 1-60; destination is an absolute ".mp4"
   path that must not exist, in an existing folder. Neither path may pass through a link. Encodes
   H.264 in an MP4 container with Windows' own Media Foundation encoder into a hidden partial file
   beside the destination, checks that it is an MP4, then renames it into place. Output is the
   video's 64-byte SHA-256. Media Foundation is loaded at call time from System32, so a Windows
   edition without it (for example an N edition without the Media Feature Pack) returns status 8
   instead of preventing this library from loading. Failure or cancellation (5) leaves no partial.
   output=NULL or capacity<64 returns 2, required=64, without filesystem work or callbacks.
   The optional synchronous callback returns nonzero to cancel; it must not unwind. */
MW_API int32_t mw_encode_video(const uint8_t *input, size_t length,
    mw_should_cancel cancel, void *context,
    uint8_t *output, size_t capacity, size_t *required);
#ifdef __cplusplus
}
#endif
#endif
