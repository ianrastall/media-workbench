//! Read-only verification of pinned files inside an application-owned folder.
use crate::artifact::{CANCELLED, HASH_MISMATCH, Progress};
use crate::path_policy::no_links;
use crate::{INVALID, IO_ERROR, valid_relative_path};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    ffi::c_void,
    fs::{self, OpenOptions},
    io::Read,
    path::PathBuf,
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    data_root: PathBuf,
    relative_path: String,
    size: u64,
    sha256: String,
}

fn report(progress: Progress, context: *mut c_void, completed: u64, total: u64) -> Result<(), i32> {
    if let Some(callback) = progress {
        // SAFETY: the ABI caller keeps the callback and context alive for this call.
        if unsafe { callback(completed, total, context) } != 0 {
            return Err(CANCELLED);
        }
    }
    Ok(())
}

pub(crate) fn verify(
    input: &[u8],
    progress: Progress,
    context: *mut c_void,
) -> Result<String, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    if !request.data_root.is_absolute()
        || !valid_relative_path(&request.relative_path)
        || request.sha256.len() != 64
        || !request
            .sha256
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
    {
        return Err(INVALID);
    }
    report(progress, context, 0, request.size)?;
    no_links(&request.data_root)?;
    let root = fs::canonicalize(&request.data_root).map_err(|_| IO_ERROR)?;
    if root.parent().is_none() || !root.is_dir() {
        return Err(INVALID);
    }
    let path = root.join(&request.relative_path);
    no_links(&path)?;
    let resolved = fs::canonicalize(&path).map_err(|_| IO_ERROR)?;
    if !resolved.starts_with(&root) {
        return Err(INVALID);
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // FILE_SHARE_READ: refuse existing writers and block write/delete while hashing.
        options.share_mode(1);
    }
    let mut file = options.open(&resolved).map_err(|_| IO_ERROR)?;
    let before = file.metadata().map_err(|_| IO_ERROR)?;
    if !before.is_file() || before.len() != request.size {
        return Err(HASH_MISMATCH);
    }
    let modified = before.modified().map_err(|_| IO_ERROR)?;
    let mut hash = Sha256::new();
    let mut chunk = [0u8; 65536];
    let mut completed = 0;
    loop {
        let count = file.read(&mut chunk).map_err(|_| IO_ERROR)?;
        if count == 0 {
            break;
        }
        completed += count as u64;
        if completed > request.size {
            return Err(HASH_MISMATCH);
        }
        hash.update(&chunk[..count]);
        report(progress, context, completed, request.size)?;
    }
    let after = file.metadata().map_err(|_| IO_ERROR)?;
    no_links(&path)?;
    if completed != request.size
        || after.len() != before.len()
        || after.modified().map_err(|_| IO_ERROR)? != modified
        || fs::canonicalize(&path).map_err(|_| IO_ERROR)? != resolved
    {
        return Err(HASH_MISMATCH);
    }
    let result = format!("{:x}", hash.finalize());
    if result != request.sha256 {
        return Err(HASH_MISMATCH);
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_declarations_do_not_touch_files() {
        for (root, path, hash, size) in [
            ("relative", "file", "a".repeat(64), "1"),
            ("C:/unused", "../escape", "a".repeat(64), "1"),
            ("C:/unused", "NUL.txt", "a".repeat(64), "1"),
            ("C:/unused", "file", "A".repeat(64), "1"),
            ("C:/unused", "file", "x".repeat(64), "1"),
            ("C:/unused", "file", "a".repeat(64), "-1"),
            ("C:/unused", "file", "a".repeat(64), "true"),
        ] {
            let input = format!(
                r#"{{"dataRoot":"{root}","relativePath":"{path}","sha256":"{hash}","size":{size}}}"#
            );
            assert_eq!(
                verify(input.as_bytes(), None, std::ptr::null_mut()),
                Err(INVALID)
            );
        }
    }

    #[test]
    fn sizing_never_reads_input_or_calls_progress() {
        unsafe extern "C" fn unexpected(_: u64, _: u64, _: *mut c_void) -> i32 {
            std::process::abort();
        }
        let mut required = 0;
        let mut output = [0u8; 63];
        // SAFETY: short-output queries do not inspect input or call the callback.
        unsafe {
            for pointer in [std::ptr::null_mut(), output.as_mut_ptr()] {
                assert_eq!(
                    crate::mw_verify_file(
                        std::ptr::null(),
                        0,
                        Some(unexpected),
                        std::ptr::null_mut(),
                        pointer,
                        63,
                        &mut required
                    ),
                    crate::BUFFER_SMALL
                );
                assert_eq!(required, 64);
            }
        }
        assert_eq!(output, [0; 63]);
    }
}
