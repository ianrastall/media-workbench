//! Media Workbench native services. Public ABI uses caller-owned UTF-8 buffers.
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{fs::File, io::Read, panic::catch_unwind, slice, str};
mod artifact;
mod drive;
mod hub_plan;
mod model_compatibility;
mod model_import;
mod path_policy;
mod relocate;
mod storage;
mod tidy;
mod verification;
mod video;

const OK: i32 = 0;
const INVALID: i32 = 1;
const BUFFER_SMALL: i32 = 2;
const IO_ERROR: i32 = 3;
const INTERNAL: i32 = 4;
const MAX_INPUT: usize = 1_048_576;

/// Pure tensor-layout compatibility policy; this never grants model readiness.
/// # Safety
/// Caller owns valid input/output/required memory as specified in the header.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_check_model_compatibility(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: caller supplies writable size_t storage.
        unsafe { *required = model_compatibility::OUTPUT_CAPACITY };
        if output.is_null() || capacity < model_compatibility::OUTPUT_CAPACITY {
            return BUFFER_SMALL;
        }
        if input.is_null() || length == 0 || length > model_compatibility::MAX_INPUT {
            return INVALID;
        }
        // SAFETY: caller supplies length readable input bytes; length is bounded above.
        let input = unsafe { slice::from_raw_parts(input, length) };
        let result = match model_compatibility::inspect(input) {
            Ok(result) => result,
            Err(code) => return code,
        };
        // SAFETY: caller supplies writable output/required storage, result lives through copy.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Plans supported public Hub file downloads without network or filesystem work.
/// # Safety
/// Caller owns valid input/output/required memory as specified in the header.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_plan_hub_model(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: caller supplies writable size_t storage.
        unsafe { *required = hub_plan::OUTPUT_CAPACITY };
        if output.is_null() || capacity < hub_plan::OUTPUT_CAPACITY {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input memory.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let result = match hub_plan::plan(input) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: output has sufficient caller-owned capacity, result lives through the copy.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

fn valid_relative_path(value: &str) -> bool {
    if value.is_empty() || value.contains([':', '\0', '\\']) || value.starts_with('/') {
        return false;
    }
    value.split('/').all(|part| {
        let stem = part.split('.').next().unwrap_or("").to_ascii_uppercase();
        let reserved = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || (stem.len() == 4
                && (stem.starts_with("COM") || stem.starts_with("LPT"))
                && matches!(stem.as_bytes()[3], b'1'..=b'9'));
        !part.is_empty()
            && part != "."
            && part != ".."
            && !reserved
            && !part.ends_with(['.', ' '])
            && !part
                .chars()
                .any(|c| c.is_control() || "<>\"|?*".contains(c))
    })
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Recipe {
    schema_version: u32,
    id: String,
    status: String,
    source: Source,
    runtime_profile_id: Option<String>,
}
#[derive(Deserialize)]
struct Source {
    provider: String,
    revision: Option<String>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Plan {
    recipe_id: String,
    can_execute: bool,
    blockers: Vec<String>,
    steps: Vec<Step>,
}
#[derive(Serialize)]
struct Step {
    id: &'static str,
    description: &'static str,
}

fn make_plan(input: &[u8]) -> Result<Vec<u8>, i32> {
    let recipe: Recipe = serde_json::from_slice(input).map_err(|_| INVALID)?;
    if !valid_relative_path(&recipe.id) || recipe.id.contains('/') {
        return Err(INVALID);
    }
    let mut blockers = Vec::new();
    if recipe.schema_version != 1 {
        blockers.push("Unsupported recipe schema.".into());
    }
    if recipe.status != "verified" && recipe.status != "experimental" {
        blockers.push("Recipe is not approved for local inference verification.".into());
    }
    if recipe.source.provider != "huggingface" {
        blockers.push("Source provider is unsupported.".into());
    }
    let pinned = recipe
        .source
        .revision
        .as_ref()
        .is_some_and(|r| r.len() == 40 && r.bytes().all(|c| c.is_ascii_hexdigit()));
    if !pinned {
        blockers.push("An immutable model commit revision must be selected.".into());
    }
    if recipe
        .runtime_profile_id
        .as_ref()
        .is_none_or(|p| p.trim().is_empty())
    {
        blockers.push("A tested, locked inference runtime profile must be selected.".into());
    }
    let plan = Plan {
        recipe_id: recipe.id,
        can_execute: blockers.is_empty(),
        blockers,
        steps: vec![
            Step {
                id: "preflight",
                description: "Validate storage, access, recipe, and hardware evidence",
            },
            Step {
                id: "runtime",
                description: "Provision the exact versioned inference runtime",
            },
            Step {
                id: "download",
                description: "Fetch selected files from the pinned model revision",
            },
            Step {
                id: "verify",
                description: "Verify files, imports, GPU execution, and model output",
            },
            Step {
                id: "commit",
                description: "Record ready state only after verification succeeds",
            },
        ],
    };
    serde_json::to_vec(&plan).map_err(|_| INTERNAL)
}

unsafe fn input_bytes<'a>(input: *const u8, length: usize) -> Result<&'a [u8], i32> {
    if input.is_null() || length == 0 || length > MAX_INPUT {
        return Err(INVALID);
    }
    // SAFETY: caller supplies readable memory for the declared length, per the C contract.
    Ok(unsafe { slice::from_raw_parts(input, length) })
}

unsafe fn copy_output(bytes: &[u8], output: *mut u8, capacity: usize, required: *mut usize) -> i32 {
    if required.is_null() {
        return INVALID;
    }
    // SAFETY: required is a writable size_t per the C contract.
    unsafe {
        *required = bytes.len();
    }
    if capacity < bytes.len() || output.is_null() {
        return BUFFER_SMALL;
    }
    // SAFETY: output points to capacity writable bytes, and does not overlap bytes.
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), output, bytes.len());
    }
    OK
}

#[unsafe(no_mangle)]
pub extern "C" fn mw_abi_version() -> u32 {
    1
}

/// # Safety
/// `input` must reference `length` readable bytes for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_validate_relative_path(input: *const u8, length: usize) -> i32 {
    catch_unwind(|| {
        // SAFETY: inherited from the caller's contract.
        let bytes = match unsafe { input_bytes(input, length) } {
            Ok(v) => v,
            Err(e) => return e,
        };
        match str::from_utf8(bytes) {
            Ok(v) if valid_relative_path(v) => OK,
            _ => INVALID,
        }
    })
    .unwrap_or(INTERNAL)
}

/// # Safety
/// Valid readable input and writable output/required pointers are required as in the header.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_plan_installation(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        // SAFETY: inherited from the caller's contract.
        let bytes = match unsafe { input_bytes(input, length) } {
            Ok(v) => v,
            Err(e) => return e,
        };
        let result = match make_plan(bytes) {
            Ok(v) => v,
            Err(e) => return e,
        };
        // SAFETY: inherited from the caller's contract; result remains alive during copying.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// # Safety
/// Path is UTF-8 readable memory; output and required satisfy the header's buffer contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_sha256_file(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: required is writable according to the caller's contract.
        unsafe {
            *required = 64;
        }
        if output.is_null() || capacity < 64 {
            return BUFFER_SMALL;
        }
        // SAFETY: inherited from the caller's contract.
        let bytes = match unsafe { input_bytes(input, length) } {
            Ok(v) => v,
            Err(e) => return e,
        };
        let path = match str::from_utf8(bytes) {
            Ok(v) if !v.contains('\0') => v,
            _ => return INVALID,
        };
        let mut file = match File::open(path) {
            Ok(v) => v,
            Err(_) => return IO_ERROR,
        };
        let mut digest = Sha256::new();
        let mut chunk = [0u8; 65536];
        loop {
            match file.read(&mut chunk) {
                Ok(0) => break,
                Ok(n) => digest.update(&chunk[..n]),
                Err(_) => return IO_ERROR,
            }
        }
        let hash = format!("{:x}", digest.finalize());
        // SAFETY: inherited from the caller's contract.
        unsafe { copy_output(hash.as_bytes(), output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Export a completed artifact through the same native service used by app/worker clients.
/// # Safety
/// Input/output follow the header contract. A callback/context must remain valid for this call;
/// callbacks must not unwind or retain native pointers. Buffer-size queries do no filesystem work.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_export_artifact(
    input: *const u8,
    length: usize,
    progress: artifact::Progress,
    context: *mut std::ffi::c_void,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: required is caller-owned writable memory.
        unsafe { *required = 64 };
        if output.is_null() || capacity < 64 {
            return BUFFER_SMALL;
        }
        // SAFETY: readable input is guaranteed by the caller.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let hash = match artifact::export(input, progress, context) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: the output is large enough and caller-owned; hash lives through this copy.
        unsafe { copy_output(hash.as_bytes(), output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Inventory application-managed storage without following links.
/// # Safety
/// Input/output follow the header contract. The optional callback/context must remain live;
/// no callback may unwind across the ABI. Size queries do no filesystem work.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_storage_summary(
    input: *const u8,
    length: usize,
    cancel: storage::Cancel,
    context: *mut std::ffi::c_void,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: caller supplies writable size_t storage.
        unsafe { *required = storage::OUTPUT_CAPACITY };
        if output.is_null() || capacity < storage::OUTPUT_CAPACITY {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input memory.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let result = match storage::summary(input, cancel, context) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: caller supplies capacity writable bytes; result is live through this copy.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Verify a pinned file without changing it or installation readiness.
/// # Safety
/// Input/output follow the header contract. Callback/context remain live for this call;
/// callbacks must not unwind. Size queries do no filesystem work or callbacks.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_verify_file(
    input: *const u8,
    length: usize,
    progress: artifact::Progress,
    context: *mut std::ffi::c_void,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: required is caller-owned writable memory.
        unsafe { *required = 64 };
        if output.is_null() || capacity < 64 {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let hash = match verification::verify(input, progress, context) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: output has sufficient writable capacity and hash remains live.
        unsafe { copy_output(hash.as_bytes(), output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Copy a user's model file into the data folder while hashing it once.
/// # Safety
/// Input/output follow the header contract. Callback/context remain live for this call;
/// callbacks must not unwind. Size queries do no filesystem work or callbacks.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_import_file(
    input: *const u8,
    length: usize,
    progress: artifact::Progress,
    context: *mut std::ffi::c_void,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: required is caller-owned writable memory.
        unsafe { *required = 64 };
        if output.is_null() || capacity < 64 {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let hash = match model_import::copy(input, progress, context) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: output has sufficient writable capacity and hash remains live.
        unsafe { copy_output(hash.as_bytes(), output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Free space and drive type (SSD, hard disk, network) for a data folder; creates nothing.
/// # Safety
/// Input/output follow the header contract. Size queries do no filesystem work.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_drive_info(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: caller supplies writable size_t storage.
        unsafe { *required = drive::OUTPUT_CAPACITY };
        if output.is_null() || capacity < drive::OUTPUT_CAPACITY {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input memory.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let result = match drive::info(input) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: caller supplies capacity writable bytes; result is live through this copy.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Bounded automatic clean-up of application-made files, under the data-folder lock.
/// # Safety
/// Input/output follow the header contract. The optional callback/context must remain live;
/// no callback may unwind across the ABI. Size queries do no filesystem work.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_tidy_data_folder(
    input: *const u8,
    length: usize,
    cancel: storage::Cancel,
    context: *mut std::ffi::c_void,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: caller supplies writable size_t storage.
        unsafe { *required = tidy::OUTPUT_CAPACITY };
        if output.is_null() || capacity < tidy::OUTPUT_CAPACITY {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input memory.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let result = match tidy::tidy(input, cancel, context) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: caller supplies capacity writable bytes; result is live through this copy.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Plan moving the data folder: what would be copied and whether it can start. Reads only.
/// # Safety
/// Input/output follow the header contract. Size queries do no filesystem work.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_plan_data_move(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: caller supplies writable size_t storage.
        unsafe { *required = relocate::OUTPUT_CAPACITY };
        if output.is_null() || capacity < relocate::OUTPUT_CAPACITY {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input memory.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let result = match relocate::plan(input) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: caller supplies capacity writable bytes; result is live through this copy.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Copy the data folder to a new location and verify every file; the source is never changed.
/// # Safety
/// Input/output follow the header contract. The optional callback/context must remain live;
/// no callback may unwind across the ABI. Size queries do no filesystem work.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_move_data_folder(
    input: *const u8,
    length: usize,
    progress: artifact::Progress,
    context: *mut std::ffi::c_void,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: caller supplies writable size_t storage.
        unsafe { *required = relocate::OUTPUT_CAPACITY };
        if output.is_null() || capacity < relocate::OUTPUT_CAPACITY {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input memory.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let result = match relocate::relocate(input, progress, context) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: caller supplies capacity writable bytes; result is live through this copy.
        unsafe { copy_output(&result, output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

/// Encode a short clip's raw RGB frames into an MP4 with Windows' own H.264 encoder.
/// # Safety
/// Input/output follow the header contract. The optional callback/context must remain live;
/// no callback may unwind across the ABI. Size queries do no filesystem work.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw_encode_video(
    input: *const u8,
    length: usize,
    cancel: storage::Cancel,
    context: *mut std::ffi::c_void,
    output: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    catch_unwind(|| {
        if required.is_null() {
            return INVALID;
        }
        // SAFETY: required is caller-owned writable memory.
        unsafe { *required = 64 };
        if output.is_null() || capacity < 64 {
            return BUFFER_SMALL;
        }
        // SAFETY: caller supplies readable input.
        let input = match unsafe { input_bytes(input, length) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        let hash = match video::encode(input, cancel, context) {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: output has sufficient writable capacity and hash remains live.
        unsafe { copy_output(hash.as_bytes(), output, capacity, required) }
    })
    .unwrap_or(INTERNAL)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn mutation_size_queries_do_no_filesystem_work() {
        let root = std::env::temp_dir().join("media-workbench-size-query-must-not-exist");
        let input = serde_json::to_vec(&serde_json::json!({
            "dataRoot": root, "runtimeProfileId": "p", "modelIds": ["m"],
            "installationVerified": true, "removeDownloadCaches": true,
        }))
        .unwrap();
        let mut size = 0;
        // SAFETY: valid input, a null output with zero capacity, and a writable size.
        unsafe {
            assert_eq!(
                mw_tidy_data_folder(
                    input.as_ptr(),
                    input.len(),
                    None,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    0,
                    &mut size
                ),
                BUFFER_SMALL
            );
            assert_eq!(size, tidy::OUTPUT_CAPACITY);
            assert_eq!(
                mw_drive_info(
                    input.as_ptr(),
                    input.len(),
                    std::ptr::null_mut(),
                    0,
                    &mut size
                ),
                BUFFER_SMALL
            );
            assert_eq!(size, drive::OUTPUT_CAPACITY);
        }
        assert!(!root.exists());
    }
    #[test]
    fn safe_model_paths() {
        for p in [
            "unet/diffusion_pytorch_model.safetensors",
            "模型/config.json",
        ] {
            assert!(valid_relative_path(p));
        }
    }
    #[test]
    fn rejects_windows_escapes_and_devices() {
        for p in [
            "../outside",
            "/root",
            "C:/root",
            "a\\b",
            "CON.txt",
            "a/NUL",
            "a/COM1.txt",
            "a//b",
            "a/.",
            "a/last.",
            "a/b ",
            "x:y",
        ] {
            assert!(!valid_relative_path(p), "{p}");
        }
    }
    #[test]
    fn draft_recipe_stays_blocked() {
        let json = br#"{"schemaVersion":1,"id":"sd15","status":"draft","source":{"provider":"huggingface","revision":null},"runtimeProfileId":null}"#;
        let result: serde_json::Value = serde_json::from_slice(&make_plan(json).unwrap()).unwrap();
        assert_eq!(result["canExecute"], false);
        assert!(result["blockers"].as_array().unwrap().len() >= 3);
    }
    #[test]
    fn pinned_experimental_recipe_can_be_verified_locally() {
        let json = br#"{"schemaVersion":1,"id":"sd15","status":"experimental","source":{"provider":"huggingface","revision":"451f4fe16113bff5a5d2269ed5ad43b0592e9a14"},"runtimeProfileId":"sd15-cu126-v1"}"#;
        let result: serde_json::Value = serde_json::from_slice(&make_plan(json).unwrap()).unwrap();
        assert_eq!(result["canExecute"], true);
        assert!(result["blockers"].as_array().unwrap().is_empty());
    }
    #[test]
    fn abi_reports_required_size_and_contains_bad_input() {
        let mut size = 0;
        let input = b"{}";
        // SAFETY: all pointers refer to valid local storage or an allowed null output.
        unsafe {
            assert_eq!(mw_validate_relative_path(std::ptr::null(), 1), INVALID);
            assert_eq!(
                mw_plan_installation(
                    input.as_ptr(),
                    input.len(),
                    std::ptr::null_mut(),
                    0,
                    &mut size
                ),
                INVALID
            );
            assert_eq!(
                mw_sha256_file(
                    input.as_ptr(),
                    input.len(),
                    std::ptr::null_mut(),
                    0,
                    &mut size
                ),
                BUFFER_SMALL
            );
        }
        assert_eq!(size, 64);
    }
}
