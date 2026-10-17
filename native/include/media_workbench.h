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
           4 internal panic contained at the boundary.
   Query output size using output=NULL, capacity=0 and a valid required pointer.
   Pointer/length pairs must refer to readable/writable memory as appropriate. */
MW_API uint32_t mw_abi_version(void);
MW_API int32_t mw_validate_relative_path(const uint8_t *input, size_t length);
MW_API int32_t mw_plan_installation(const uint8_t *input, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
MW_API int32_t mw_sha256_file(const uint8_t *path, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
#ifdef __cplusplus
}
#endif
#endif
