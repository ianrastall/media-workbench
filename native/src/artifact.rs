//! Completed-artifact validation, containment, and cancellable verified export.
use crate::path_policy::no_links;
use crate::{INVALID, IO_ERROR, MAX_INPUT, valid_relative_path};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    ffi::c_void,
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};

pub(crate) const CANCELLED: i32 = 5;
pub(crate) const HASH_MISMATCH: i32 = 6;
pub(crate) type Progress = Option<unsafe extern "C" fn(u64, u64, *mut c_void) -> i32>;
static NEXT_TEMP: AtomicU64 = AtomicU64::new(0);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ExportRequest {
    data_root: PathBuf,
    job_id: String,
    destination: PathBuf,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct CompletedJob {
    id: String,
    status: String,
    image_path: String,
    image_sha256: String,
    #[serde(default)]
    video_path: Option<String>,
    #[serde(default)]
    video_sha256: Option<String>,
}

fn valid_id(value: &str) -> bool {
    value.len() == 32
        && value
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

struct Temporary(PathBuf);
impl Drop for Temporary {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

fn temporary(parent: &Path) -> Result<(Temporary, File), i32> {
    for _ in 0..32 {
        let sequence = NEXT_TEMP.fetch_add(1, Ordering::Relaxed);
        let path = parent.join(format!(
            ".media-workbench-{}-{sequence}.partial",
            std::process::id()
        ));
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((Temporary(path), file)),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(_) => return Err(IO_ERROR),
        }
    }
    Err(IO_ERROR)
}

fn report(progress: Progress, context: *mut c_void, completed: u64, total: u64) -> Result<(), i32> {
    if let Some(callback) = progress {
        // SAFETY: the ABI caller guarantees a callable function and live context for this call.
        if unsafe { callback(completed, total, context) } != 0 {
            return Err(CANCELLED);
        }
    }
    Ok(())
}

pub(crate) fn export(
    input: &[u8],
    progress: Progress,
    context: *mut c_void,
) -> Result<String, i32> {
    let request: ExportRequest = serde_json::from_slice(input).map_err(|_| INVALID)?;
    if !valid_id(&request.job_id)
        || !request.data_root.is_absolute()
        || !request.destination.is_absolute()
    {
        return Err(INVALID);
    }
    no_links(&request.data_root)?;
    let root = fs::canonicalize(&request.data_root).map_err(|_| IO_ERROR)?;
    if root.parent().is_none() || !root.is_dir() {
        return Err(INVALID);
    }
    // Canonicalize the existing parent, since a new destination does not exist yet.
    let destination = if request.destination.exists() {
        fs::canonicalize(&request.destination).map_err(|_| IO_ERROR)?
    } else {
        let parent = request.destination.parent().ok_or(INVALID)?;
        fs::canonicalize(parent)
            .map_err(|_| IO_ERROR)?
            .join(request.destination.file_name().ok_or(INVALID)?)
    };
    let extension = destination
        .extension()
        .and_then(|ext| ext.to_str())
        .map(str::to_ascii_lowercase);
    let video = extension.as_deref() == Some("mp4");
    if destination.starts_with(&root) || !(video || extension.as_deref() == Some("png")) {
        return Err(INVALID);
    }
    let job_path = root.join("jobs").join(format!("{}.json", request.job_id));
    no_links(&job_path)?;
    let job_file = File::open(job_path).map_err(|_| IO_ERROR)?;
    let mut bytes = Vec::new();
    job_file
        .take(MAX_INPUT as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| IO_ERROR)?;
    if bytes.len() > MAX_INPUT {
        return Err(INVALID);
    }
    let job: CompletedJob = serde_json::from_slice(&bytes).map_err(|_| INVALID)?;
    // A video job exports its MP4; its poster frame is the image of every other client path.
    let (stored_path, expected) = if video {
        (
            job.video_path.as_deref().ok_or(INVALID)?,
            job.video_sha256.as_deref().ok_or(INVALID)?,
        )
    } else {
        (job.image_path.as_str(), job.image_sha256.as_str())
    };
    if job.id != request.job_id
        || job.status != "succeeded"
        || expected.len() != 64
        || !expected
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
        || (video && Path::new(stored_path).is_absolute())
    {
        return Err(INVALID);
    }
    let stored = Path::new(stored_path);
    let relative = if stored.is_absolute() {
        // Legacy absolute records re-root by their trailing job ID and filename.
        PathBuf::from(stored.parent().and_then(Path::file_name).ok_or(INVALID)?)
            .join(stored.file_name().ok_or(INVALID)?)
    } else {
        PathBuf::from(stored_path)
    };
    let relative_text = relative.to_str().ok_or(INVALID)?.replace('\\', "/");
    if !valid_relative_path(&relative_text)
        || relative.components().next().map(|part| part.as_os_str())
            != Some(std::ffi::OsStr::new(&request.job_id))
    {
        return Err(INVALID);
    }
    let source_path = root.join("artifacts").join(relative);
    no_links(&source_path)?;
    let source_path = fs::canonicalize(source_path).map_err(|_| IO_ERROR)?;
    if !source_path.starts_with(root.join("artifacts").join(&request.job_id)) {
        return Err(INVALID);
    }
    let mut source = File::open(source_path).map_err(|_| IO_ERROR)?;
    let total = source.metadata().map_err(|_| IO_ERROR)?.len();
    report(progress, context, 0, total)?;
    let (temporary, mut output) = temporary(destination.parent().ok_or(INVALID)?)?;
    let mut hash = Sha256::new();
    let mut block = vec![0; 1024 * 1024];
    let mut completed = 0;
    loop {
        let read = source.read(&mut block).map_err(|_| IO_ERROR)?;
        if read == 0 {
            break;
        }
        output.write_all(&block[..read]).map_err(|_| IO_ERROR)?;
        hash.update(&block[..read]);
        completed += read as u64;
        report(progress, context, completed, total)?;
    }
    let actual = format!("{:x}", hash.finalize());
    if actual != expected {
        return Err(HASH_MISMATCH);
    }
    output.sync_all().map_err(|_| IO_ERROR)?;
    drop(output);
    drop(source);
    report(progress, context, completed, total)?;
    fs::rename(&temporary.0, destination).map_err(|_| IO_ERROR)?;
    Ok(actual)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_incomplete_requests_without_filesystem_work() {
        for input in [b"{}".as_slice(), b"[]", b"null"] {
            assert_eq!(export(input, None, std::ptr::null_mut()), Err(INVALID));
        }
    }

    #[test]
    fn strict_job_ids() {
        assert!(valid_id("0123456789abcdef0123456789abcdef"));
        for value in ["../escape", "", "0123456789ABCDEF0123456789ABCDEF"] {
            assert!(!valid_id(value));
        }
    }
}
