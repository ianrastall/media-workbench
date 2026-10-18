//! Cancellable storage inventory. No model/runtime packages or worker process are needed.
use crate::{
    INVALID, IO_ERROR,
    artifact::CANCELLED,
    path_policy::{is_link, no_links},
    valid_relative_path,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    ffi::c_void,
    fs,
    path::{Component, Path, PathBuf},
};

pub(crate) const OUTPUT_CAPACITY: usize = 8192;
pub(crate) type Cancel = Option<unsafe extern "C" fn(*mut c_void) -> i32>;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    data_root: PathBuf,
    runtime_profile_id: String,
    model_ids: Vec<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Item {
    id: &'static str,
    label: &'static str,
    bytes: u64,
    files: u64,
    clearable: bool,
    note: &'static str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    free_bytes: u64,
    total_bytes: u64,
    installation_bytes: u64,
    items: Vec<Item>,
}

fn cancelled(cancel: Cancel, context: *mut c_void) -> Result<(), i32> {
    if let Some(callback) = cancel {
        // SAFETY: caller supplies a live, non-unwinding callback/context for this synchronous call.
        if unsafe { callback(context) } != 0 {
            return Err(CANCELLED);
        }
    }
    Ok(())
}

fn add(left: u64, right: u64) -> Result<u64, i32> {
    // The managed/wire contracts use signed 64-bit counts.
    left.checked_add(right)
        .filter(|value| *value <= i64::MAX as u64)
        .ok_or(IO_ERROR)
}

fn measure(path: &Path, cancel: Cancel, context: *mut c_void) -> Result<(u64, u64), i32> {
    let mut stack = vec![path.to_path_buf()];
    let (mut bytes, mut files) = (0, 0);
    while let Some(folder) = stack.pop() {
        cancelled(cancel, context)?;
        let metadata = match fs::symlink_metadata(&folder) {
            Ok(value) => value,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
            Err(_) => return Err(IO_ERROR),
        };
        if is_link(&metadata) || !metadata.is_dir() {
            continue;
        }
        no_links(&folder)?;
        let entries = match fs::read_dir(&folder) {
            Ok(value) => value,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
            Err(_) => return Err(IO_ERROR),
        };
        for entry in entries {
            cancelled(cancel, context)?;
            let entry = match entry {
                Ok(value) => value,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
                Err(_) => return Err(IO_ERROR),
            };
            // Does not follow links; Windows reuses metadata already returned by enumeration.
            let metadata = match entry.metadata() {
                Ok(value) => value,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
                Err(_) => return Err(IO_ERROR),
            };
            if is_link(&metadata) {
                continue;
            }
            if metadata.is_dir() {
                stack.push(entry.path());
            } else if metadata.is_file() {
                bytes = add(bytes, metadata.len())?;
                files = add(files, 1)?;
            }
        }
    }
    Ok((bytes, files))
}

#[cfg(windows)]
fn disk_space(root: &Path) -> Result<(u64, u64), i32> {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        #[link_name = "GetDiskFreeSpaceExW"]
        fn get_disk_free_space_ex(
            path: *const u16,
            available: *mut u64,
            total: *mut u64,
            free: *mut u64,
        ) -> i32;
    }
    // A trailing separator also handles UNC share directories.
    let mut path: Vec<u16> = root.as_os_str().encode_wide().collect();
    path.extend([b'\\' as u16, 0]);
    let (mut available, mut total) = (0, 0);
    // SAFETY: path is terminated UTF-16; both u64 outputs are live and writable.
    if unsafe {
        get_disk_free_space_ex(
            path.as_ptr(),
            &mut available,
            &mut total,
            std::ptr::null_mut(),
        )
    } == 0
    {
        return Err(IO_ERROR);
    }
    Ok((available, total))
}

#[cfg(not(windows))]
fn disk_space(_root: &Path) -> Result<(u64, u64), i32> {
    Err(IO_ERROR) // This application/service targets Windows x64.
}

pub(crate) fn summary(input: &[u8], cancel: Cancel, context: *mut c_void) -> Result<Vec<u8>, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    let valid_id = |id: &str| id.len() <= 128 && !id.contains('/') && valid_relative_path(id);
    if !valid_id(&request.runtime_profile_id)
        || request.model_ids.is_empty()
        || request.model_ids.len() > 32
        || request.model_ids.iter().any(|id| !valid_id(id))
        || request.model_ids.iter().collect::<HashSet<_>>().len() != request.model_ids.len()
        || !request.data_root.is_absolute()
        || !request
            .data_root
            .components()
            .any(|part| matches!(part, Component::Normal(_)))
        || request
            .data_root
            .components()
            .any(|part| matches!(part, Component::ParentDir))
        || request
            .data_root
            .as_os_str()
            .to_string_lossy()
            .contains('\0')
    {
        return Err(INVALID);
    }
    #[cfg(windows)]
    if request.data_root.components().any(|part| matches!(part, Component::Prefix(prefix) if matches!(prefix.kind(), std::path::Prefix::DeviceNS(_)))) {
        return Err(INVALID);
    }
    cancelled(cancel, context)?;
    no_links(&request.data_root)?;
    fs::create_dir_all(&request.data_root).map_err(|_| IO_ERROR)?;
    let root = fs::canonicalize(request.data_root).map_err(|_| IO_ERROR)?;
    no_links(&root)?;
    if root.parent().is_none() || !root.is_dir() {
        return Err(INVALID);
    }
    let definitions = [
        (
            "runtime",
            "Inference runtime",
            vec![format!("runtimes/{}", request.runtime_profile_id)],
            "",
        ),
        (
            "model",
            "Image models (Stable Diffusion 1.5 and upscaler)",
            request
                .model_ids
                .iter()
                .map(|id| format!("models/{id}"))
                .collect(),
            "",
        ),
        (
            "caches",
            "Download caches",
            vec!["cache".into(), "bootstrap".into()],
            "Packages and archives kept so a repair need not download them again. Install / verify downloads them again if needed.",
        ),
        ("images", "Images", vec!["artifacts".into()], ""),
        (
            "logs",
            "Logs",
            vec!["logs".into()],
            "Inference logs. Library's Open log buttons stop working for the removed logs.",
        ),
        ("jobs", "Job records", vec!["jobs".into()], ""),
        ("library", "Library index", vec!["library".into()], ""),
    ];
    let mut items = Vec::new();
    let mut installation_bytes = 0;
    for (id, label, folders, note) in definitions {
        let (mut bytes, mut files) = (0, 0);
        for folder in folders {
            cancelled(cancel, context)?;
            let path = root.join(folder);
            no_links(&path)?;
            let (size, count) = measure(&path, cancel, context)?;
            bytes = add(bytes, size)?;
            files = add(files, count)?;
        }
        if matches!(id, "runtime" | "model" | "caches") {
            installation_bytes = add(installation_bytes, bytes)?;
        }
        items.push(Item {
            id,
            label,
            bytes,
            files,
            clearable: !note.is_empty(),
            note,
        });
    }
    cancelled(cancel, context)?;
    let (free_bytes, total_bytes) = disk_space(&root)?;
    serde_json::to_vec(&Snapshot {
        free_bytes,
        total_bytes,
        installation_bytes,
        items,
    })
    .map_err(|_| crate::INTERNAL)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rejects_invalid_layout_before_touching_storage() {
        for input in [
            br#"{}"#.as_slice(),
            br#"{"dataRoot":"relative","runtimeProfileId":"p","modelIds":["m"]}"#,
            br#"{"dataRoot":"C:/unused","runtimeProfileId":"../escape","modelIds":["m"]}"#,
            br#"{"dataRoot":"C:/unused","runtimeProfileId":"p","modelIds":["m","m"]}"#,
        ] {
            assert_eq!(summary(input, None, std::ptr::null_mut()), Err(INVALID));
        }
    }
    #[test]
    fn counts_cannot_overflow_the_managed_contract() {
        assert_eq!(add(i64::MAX as u64, 1), Err(IO_ERROR));
        assert_eq!(add(u64::MAX, 1), Err(IO_ERROR));
    }
}
