//! One-pass copy and SHA-256 of a user's model file into the application-owned data folder.
use crate::artifact::{CANCELLED, HASH_MISMATCH, Progress};
use crate::path_policy::no_links;
use crate::{INVALID, IO_ERROR, valid_relative_path};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    ffi::c_void,
    fs::{self, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    source: PathBuf,
    data_root: PathBuf,
    relative_path: String,
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

/// Removes the partial copy unless the copy was committed.
struct Partial(Option<PathBuf>);
impl Drop for Partial {
    fn drop(&mut self) {
        if let Some(path) = self.0.take() {
            let _ = fs::remove_file(path);
        }
    }
}

fn contained_parent(root: &Path, destination: &Path) -> Result<(), i32> {
    let parent = destination.parent().ok_or(INVALID)?;
    no_links(parent)?;
    fs::create_dir_all(parent).map_err(|_| IO_ERROR)?;
    no_links(parent)?;
    let resolved = fs::canonicalize(parent).map_err(|_| IO_ERROR)?;
    if !resolved.starts_with(root) {
        return Err(INVALID);
    }
    Ok(())
}

pub(crate) fn copy(input: &[u8], progress: Progress, context: *mut c_void) -> Result<String, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    if !request.source.is_absolute()
        || !request.data_root.is_absolute()
        || !valid_relative_path(&request.relative_path)
    {
        return Err(INVALID);
    }
    no_links(&request.data_root)?;
    let root = fs::canonicalize(&request.data_root).map_err(|_| IO_ERROR)?;
    if root.parent().is_none() || !root.is_dir() {
        return Err(INVALID);
    }
    let destination = root.join(&request.relative_path);
    let partial = root.join(format!("{}.partial", request.relative_path));
    contained_parent(&root, &destination)?;
    if fs::symlink_metadata(&destination).is_ok() {
        return Err(INVALID); // Imports fill a fresh folder; never replace a verified file.
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // FILE_SHARE_READ: refuse an open writer and block writes/deletion while copying.
        options.share_mode(1);
    }
    let mut source = options.open(&request.source).map_err(|_| IO_ERROR)?;
    let before = source.metadata().map_err(|_| IO_ERROR)?;
    if !before.is_file() {
        return Err(INVALID);
    }
    let modified = before.modified().map_err(|_| IO_ERROR)?;
    let total = before.len();
    report(progress, context, 0, total)?;
    let _ = fs::remove_file(&partial); // Left by an interrupted earlier attempt in this fresh folder.
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&partial)
        .map_err(|_| IO_ERROR)?;
    let mut guard = Partial(Some(partial.clone()));
    let mut hash = Sha256::new();
    let mut block = vec![0; 1024 * 1024];
    let mut completed = 0;
    loop {
        let count = source.read(&mut block).map_err(|_| IO_ERROR)?;
        if count == 0 {
            break;
        }
        completed += count as u64;
        if completed > total {
            return Err(HASH_MISMATCH);
        }
        output.write_all(&block[..count]).map_err(|_| IO_ERROR)?;
        hash.update(&block[..count]);
        report(progress, context, completed, total)?;
    }
    let after = source.metadata().map_err(|_| IO_ERROR)?;
    if completed != total
        || after.len() != total
        || after.modified().map_err(|_| IO_ERROR)? != modified
    {
        return Err(HASH_MISMATCH); // The user's file changed while it was being copied.
    }
    output.sync_all().map_err(|_| IO_ERROR)?;
    drop(output);
    drop(source);
    no_links(&partial)?;
    fs::rename(&partial, &destination).map_err(|_| IO_ERROR)?;
    guard.0 = None;
    Ok(format!("{:x}", hash.finalize()))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn folder(name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "mw-import-{name}-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        fs::canonicalize(path).unwrap()
    }

    fn request(source: &Path, root: &Path, relative: &str) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({
            "source": source, "dataRoot": root, "relativePath": relative
        }))
        .unwrap()
    }

    #[test]
    fn invalid_declarations_do_not_touch_files() {
        for input in [
            br#"{}"#.as_slice(),
            br#"{"source":"relative","dataRoot":"C:/unused","relativePath":"a"}"#,
            br#"{"source":"C:/a","dataRoot":"relative","relativePath":"a"}"#,
            br#"{"source":"C:/a","dataRoot":"C:/unused","relativePath":"../a"}"#,
            br#"{"source":"C:/a","dataRoot":"C:/unused","relativePath":"NUL"}"#,
        ] {
            assert_eq!(copy(input, None, std::ptr::null_mut()), Err(INVALID));
        }
    }

    #[test]
    fn copies_hashes_and_refuses_to_replace() {
        let base = folder("copy");
        let source = base.join("user model.safetensors");
        fs::write(&source, b"abc").unwrap();
        let root = base.join("data");
        fs::create_dir_all(&root).unwrap();
        let input = request(&source, &root, "models/imported/x/model.safetensors");
        assert_eq!(
            copy(&input, None, std::ptr::null_mut()).unwrap(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        let copied = root.join("models/imported/x/model.safetensors");
        assert_eq!(fs::read(&copied).unwrap(), b"abc");
        assert_eq!(fs::read(&source).unwrap(), b"abc");
        assert_eq!(copy(&input, None, std::ptr::null_mut()), Err(INVALID));
        assert_eq!(fs::read(&copied).unwrap(), b"abc");
        let _ = fs::remove_dir_all(base);
    }

    #[test]
    fn cancellation_leaves_no_partial_copy() {
        unsafe extern "C" fn stop(_: u64, _: u64, _: *mut c_void) -> i32 {
            1
        }
        let base = folder("cancel");
        let source = base.join("model.safetensors");
        fs::write(&source, vec![7u8; 3 * 1024 * 1024]).unwrap();
        let root = base.join("data");
        fs::create_dir_all(&root).unwrap();
        let input = request(&source, &root, "models/imported/y/model.safetensors");
        assert_eq!(
            copy(&input, Some(stop), std::ptr::null_mut()),
            Err(CANCELLED)
        );
        let folder = root.join("models/imported/y");
        assert_eq!(fs::read_dir(folder).unwrap().count(), 0);
        let _ = fs::remove_dir_all(base);
    }
}
