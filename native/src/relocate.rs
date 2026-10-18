//! Moving a data folder to another location: a verified copy, never a delete.
//!
//! The copy is written into a marked staging folder beside the destination while the source's
//! data-folder lock is held, so setup, generation, imports and clean-up cannot change it meanwhile.
//! Every file is hashed while it is read, re-read from the destination and compared, and keeps its
//! modification time (readiness stamps compare size and time). Only when every file matches is the
//! staging folder renamed to the destination. The source is never changed; removing it is the
//! user's own, separate decision. A crash leaves only the marked staging folder, which the next
//! attempt (or an explicit abandon) removes.
use crate::{
    INVALID, IO_ERROR,
    artifact::{CANCELLED, HASH_MISMATCH, Progress},
    path_policy::{is_link, no_links},
    storage::lexically_valid_root,
    tidy::Lock,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    ffi::c_void,
    fs::{self, File, OpenOptions},
    io::{ErrorKind, Read, Write},
    path::{Path, PathBuf},
    time::SystemTime,
};

pub(crate) const OUTPUT_CAPACITY: usize = 4096;
/// The destination drive keeps at least this much free after the copy.
pub(crate) const RESERVE_BYTES: u64 = 1 << 30;
/// Top-level entries that are not copied: the lock, temporary work, the rebuildable Library index,
/// and a clean-up that was interrupted.
const SKIPPED: [&str; 4] = [".operation.lock", "temp", "library", ".removing"];
/// Written first into a staging folder, so only a folder this code made is ever removed as one.
const MARKER: &str = ".media-workbench-move";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    source: PathBuf,
    destination: PathBuf,
    /// Only remove a staging folder an interrupted move left behind.
    #[serde(default)]
    abandon: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Plan {
    files: u64,
    bytes: u64,
    free_bytes: u64,
    needed_bytes: u64,
    same_volume: bool,
    /// An interrupted earlier move left a staging folder; moving removes it first.
    leftover: bool,
    /// Empty when the move can start; otherwise the plain reason it cannot.
    blocker: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Moved {
    files: u64,
    bytes: u64,
    destination: String,
}

struct Entry {
    relative: PathBuf,
    bytes: u64,
    modified: SystemTime,
}

struct Inventory {
    folders: Vec<PathBuf>,
    files: Vec<Entry>,
    bytes: u64,
    /// A link or junction inside the data folder; it is never followed, so the move refuses.
    link: Option<PathBuf>,
}

fn inventory(source: &Path) -> Result<Inventory, i32> {
    let mut result = Inventory {
        folders: Vec::new(),
        files: Vec::new(),
        bytes: 0,
        link: None,
    };
    let mut stack = vec![PathBuf::new()];
    while let Some(relative) = stack.pop() {
        let listing = fs::read_dir(source.join(&relative)).map_err(|_| IO_ERROR)?;
        for entry in listing {
            let entry = entry.map_err(|_| IO_ERROR)?;
            let name = entry.file_name();
            if relative.as_os_str().is_empty() && SKIPPED.iter().any(|skip| name == *skip) {
                continue;
            }
            let path = relative.join(&name);
            let metadata = entry.metadata().map_err(|_| IO_ERROR)?;
            if is_link(&metadata) {
                result.link.get_or_insert(path);
            } else if metadata.is_dir() {
                result.folders.push(path.clone());
                stack.push(path);
            } else if metadata.is_file() {
                result.bytes = result.bytes.checked_add(metadata.len()).ok_or(IO_ERROR)?;
                result.files.push(Entry {
                    relative: path,
                    bytes: metadata.len(),
                    modified: metadata.modified().map_err(|_| IO_ERROR)?,
                });
            }
        }
    }
    result.folders.sort();
    Ok(result)
}

struct Locations {
    source: PathBuf,
    destination: PathBuf,
    staging: PathBuf,
}

/// Checks both paths lexically and against links, without creating anything.
fn locations(request: &Request) -> Result<Locations, i32> {
    if !lexically_valid_root(&request.source) || !lexically_valid_root(&request.destination) {
        return Err(INVALID);
    }
    no_links(&request.source)?;
    no_links(&request.destination)?;
    let source = fs::canonicalize(&request.source).map_err(|_| IO_ERROR)?;
    if !source.is_dir() || source.parent().is_none() {
        return Err(INVALID);
    }
    // A destination that does not exist yet is resolved through its nearest existing folder.
    let existing = request
        .destination
        .ancestors()
        .find(|path| path.is_dir())
        .ok_or(IO_ERROR)?;
    let rest = request
        .destination
        .strip_prefix(existing)
        .map_err(|_| INVALID)?;
    let destination = fs::canonicalize(existing).map_err(|_| IO_ERROR)?.join(rest);
    let name = destination.file_name().ok_or(INVALID)?.to_string_lossy();
    let staging = destination
        .parent()
        .ok_or(INVALID)?
        .join(format!(".{name}.moving"));
    Ok(Locations {
        source,
        destination,
        staging,
    })
}

fn empty_or_missing(path: &Path) -> Result<bool, i32> {
    match fs::read_dir(path) {
        Ok(mut listing) => Ok(listing.next().is_none()),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(true),
        Err(_) => Err(IO_ERROR),
    }
}

fn nearest_existing(path: &Path) -> Result<&Path, i32> {
    path.ancestors().find(|p| p.is_dir()).ok_or(IO_ERROR)
}

fn plain_size(bytes: u64) -> String {
    format!("{:.1} GB", bytes as f64 / 1e9)
}

fn plan_for(places: &Locations, found: &Inventory) -> Result<Plan, i32> {
    let target = nearest_existing(&places.destination)?;
    let (free_bytes, _) = crate::drive::space(target)?;
    let needed_bytes = found.bytes.saturating_add(RESERVE_BYTES);
    let same_volume = crate::drive::volume(&places.source)
        .eq_ignore_ascii_case(&crate::drive::volume(target))
        && !crate::drive::volume(target).is_empty();
    let blocker = if places.destination == places.source {
        "That is already the data folder.".to_owned()
    } else if places.destination.starts_with(&places.source) {
        "Choose a folder outside the current data folder.".to_owned()
    } else if places.source.starts_with(&places.destination) {
        "Choose a folder that does not contain the current data folder.".to_owned()
    } else if !empty_or_missing(&places.destination)? {
        "The new folder already has files in it. Choose an empty or new folder.".to_owned()
    } else if let Some(link) = &found.link {
        format!(
            "The data folder contains a link or junction ({}), which Media Workbench never follows. Remove it, then try again.",
            link.display()
        )
    } else if free_bytes < needed_bytes {
        format!(
            "The new location needs {} free ({} of data plus {} to spare), but only {} is free there. Free up space or choose another drive.",
            plain_size(needed_bytes),
            plain_size(found.bytes),
            plain_size(RESERVE_BYTES),
            plain_size(free_bytes)
        )
    } else {
        String::new()
    };
    Ok(Plan {
        files: found.files.len() as u64,
        bytes: found.bytes,
        free_bytes,
        needed_bytes,
        same_volume,
        leftover: places.staging.join(MARKER).is_file(),
        blocker,
    })
}

/// What a move would copy and whether it can start; reads only.
pub(crate) fn plan(input: &[u8]) -> Result<Vec<u8>, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    let places = locations(&request)?;
    let found = inventory(&places.source)?;
    serde_json::to_vec(&plan_for(&places, &found)?).map_err(|_| crate::INTERNAL)
}

/// Removes a staging folder only when it carries this code's marker; links inside are removed, not followed.
fn discard_staging(staging: &Path) -> Result<(), i32> {
    match fs::symlink_metadata(staging) {
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(_) => return Err(IO_ERROR),
        Ok(metadata) if is_link(&metadata) || !metadata.is_dir() => return Err(INVALID),
        Ok(_) => {}
    }
    if !staging.join(MARKER).is_file() {
        return Err(INVALID); // Not a folder this code made.
    }
    // remove_dir_all does not follow junctions or symbolic links on Windows.
    fs::remove_dir_all(staging).map_err(|_| IO_ERROR)
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

/// Copies one file while hashing what was read, then re-reads the copy and compares.
fn copy_verified(
    from: &Path,
    to: &Path,
    entry: &Entry,
    done: &mut u64,
    total: u64,
    progress: Progress,
    context: *mut c_void,
) -> Result<(), i32> {
    let mut buffer = vec![0u8; 1 << 20];
    let mut source = File::open(from).map_err(|_| IO_ERROR)?;
    let mut target = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(to)
        .map_err(|_| IO_ERROR)?;
    let mut read_hash = Sha256::new();
    let mut copied = 0u64;
    loop {
        let count = source.read(&mut buffer).map_err(|_| IO_ERROR)?;
        if count == 0 {
            break;
        }
        read_hash.update(&buffer[..count]);
        target.write_all(&buffer[..count]).map_err(|_| IO_ERROR)?;
        copied += count as u64;
        *done += count as u64;
        report(progress, context, *done, total)?;
    }
    if copied != entry.bytes {
        return Err(IO_ERROR); // The file changed while it was copied.
    }
    target.sync_all().map_err(|_| IO_ERROR)?;
    target.set_modified(entry.modified).map_err(|_| IO_ERROR)?;
    drop(target);
    let mut written = File::open(to).map_err(|_| IO_ERROR)?;
    let mut written_hash = Sha256::new();
    loop {
        let count = written.read(&mut buffer).map_err(|_| IO_ERROR)?;
        if count == 0 {
            break;
        }
        written_hash.update(&buffer[..count]);
        *done += count as u64;
        report(progress, context, *done, total)?;
    }
    if read_hash.finalize() != written_hash.finalize() {
        return Err(HASH_MISMATCH);
    }
    Ok(())
}

fn copy_all(
    places: &Locations,
    found: &Inventory,
    progress: Progress,
    context: *mut c_void,
) -> Result<(), i32> {
    let staging = &places.staging;
    fs::create_dir_all(staging).map_err(|_| IO_ERROR)?;
    File::create(staging.join(MARKER)).map_err(|_| IO_ERROR)?;
    no_links(staging)?;
    for folder in &found.folders {
        fs::create_dir_all(staging.join(folder)).map_err(|_| IO_ERROR)?;
    }
    // Both the copy and its verification read every byte.
    let total = found.bytes.saturating_mul(2);
    let mut done = 0u64;
    let mut remaining = found.bytes;
    report(progress, context, 0, total)?;
    for entry in &found.files {
        // Space can disappear while copying; stop before the destination drive fills up.
        let (free, _) = crate::drive::space(staging)?;
        if free < remaining.saturating_add(RESERVE_BYTES / 2) {
            return Err(IO_ERROR);
        }
        copy_verified(
            &places.source.join(&entry.relative),
            &staging.join(&entry.relative),
            entry,
            &mut done,
            total,
            progress,
            context,
        )?;
        remaining -= entry.bytes;
    }
    fs::remove_file(staging.join(MARKER)).map_err(|_| IO_ERROR)?;
    // An empty destination folder made by the user is replaced by the verified copy.
    match fs::remove_dir(&places.destination) {
        Ok(()) => {}
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(_) => return Err(IO_ERROR),
    }
    fs::rename(staging, &places.destination).map_err(|_| IO_ERROR)
}

/// Copies the data folder to an empty or new destination and verifies every file. The source is
/// left exactly as it was. With `abandon`, only removes a staging folder an interrupted move left.
pub(crate) fn relocate(
    input: &[u8],
    progress: Progress,
    context: *mut c_void,
) -> Result<Vec<u8>, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    let places = locations(&request)?;
    if request.abandon {
        discard_staging(&places.staging)?;
        return serde_json::to_vec(&Moved {
            files: 0,
            bytes: 0,
            destination: String::new(),
        })
        .map_err(|_| crate::INTERNAL);
    }
    let _lock = Lock::acquire(&places.source)?;
    let found = inventory(&places.source)?;
    if !plan_for(&places, &found)?.blocker.is_empty() {
        return Err(INVALID);
    }
    discard_staging(&places.staging)?;
    if let Err(code) = copy_all(&places, &found, progress, context) {
        // Best effort: a failed clean-up leaves the marked folder for the next attempt.
        if places.staging.exists() {
            let _ = File::create(places.staging.join(MARKER));
            let _ = discard_staging(&places.staging);
        }
        return Err(code);
    }
    serde_json::to_vec(&Moved {
        files: found.files.len() as u64,
        bytes: found.bytes,
        destination: places.destination.to_string_lossy().into_owned(),
    })
    .map_err(|_| crate::INTERNAL)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Removed {
    removed_files: u64,
    removed_bytes: u64,
    /// Files kept because the new folder has no identical copy of them (or they could not be removed).
    kept_files: u64,
    /// The old folder itself is gone.
    folder_removed: bool,
}

/// After a move, removes the old data folder's files that the new folder holds with the same size
/// and modification time (the move verified their bytes and kept their times). The temporary-work
/// folder, Library index and lock are the application's own and go too. Anything else is kept, and
/// so are the folders that still hold something. Runs under the old folder's lock.
pub(crate) fn remove_moved(
    input: &[u8],
    cancel: crate::storage::Cancel,
    context: *mut c_void,
) -> Result<Vec<u8>, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    let places = locations(&request)?;
    if places.destination == places.source
        || places.destination.starts_with(&places.source)
        || places.source.starts_with(&places.destination)
        || !places.destination.is_dir()
    {
        return Err(INVALID);
    }
    no_links(&places.destination)?;
    let mut report = Removed {
        removed_files: 0,
        removed_bytes: 0,
        kept_files: 0,
        folder_removed: false,
    };
    {
        let _lock = Lock::acquire(&places.source)?;
        let found = inventory(&places.source)?;
        if found.link.is_some() {
            return Err(INVALID); // Never follow or remove through a link.
        }
        for entry in &found.files {
            crate::storage::cancelled(cancel, context)?;
            let copy = places.destination.join(&entry.relative);
            let same = fs::symlink_metadata(&copy).is_ok_and(|metadata| {
                metadata.is_file()
                    && !is_link(&metadata)
                    && metadata.len() == entry.bytes
                    && metadata.modified().ok() == Some(entry.modified)
            });
            if same && fs::remove_file(places.source.join(&entry.relative)).is_ok() {
                report.removed_files += 1;
                report.removed_bytes += entry.bytes;
            } else {
                report.kept_files += 1;
            }
        }
        for owned in ["temp", "library", ".removing"] {
            let path = places.source.join(owned);
            let owned_folder = fs::symlink_metadata(&path)
                .is_ok_and(|metadata| metadata.is_dir() && !is_link(&metadata));
            if owned_folder && fs::remove_dir_all(&path).is_err() {
                report.kept_files += 1;
            }
        }
        // Deepest folders first; a folder that still holds something stays.
        for folder in found.folders.iter().rev() {
            let _ = fs::remove_dir(places.source.join(folder));
        }
    }
    // The lock is released; its file goes last, then the folder if nothing else is left.
    let _ = fs::remove_file(places.source.join(".operation.lock"));
    report.folder_removed = fs::remove_dir(&places.source).is_ok();
    serde_json::to_vec(&report).map_err(|_| crate::INTERNAL)
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    use std::time::Duration;

    fn folder(name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "media-workbench-relocate-{name}-{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        fs::canonicalize(path).unwrap()
    }

    fn write(root: &Path, relative: &str, body: &[u8]) {
        let path = root.join(relative);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, body).unwrap();
    }

    fn request(source: &Path, destination: &Path) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({ "source": source, "destination": destination }))
            .unwrap()
    }

    fn data(root: &Path) -> PathBuf {
        let source = root.join("data");
        write(
            &source,
            "models/sd15-baseline/unet/weights.bin",
            &[7u8; 300_000],
        );
        write(&source, "jobs/install-sd15.json", br#"{"status":"ready"}"#);
        write(&source, "artifacts/abc/image.png", b"png");
        write(&source, "temp/work/x/scratch.bin", b"scratch");
        write(&source, "library/index.sqlite", b"index");
        write(&source, ".operation.lock", b"");
        let old = SystemTime::now() - Duration::from_secs(86_400);
        File::options()
            .write(true)
            .open(source.join("models/sd15-baseline/unet/weights.bin"))
            .unwrap()
            .set_modified(old)
            .unwrap();
        source
    }

    fn snapshot(root: &Path) -> Vec<(PathBuf, Vec<u8>)> {
        let mut found = Vec::new();
        let mut stack = vec![root.to_path_buf()];
        while let Some(folder) = stack.pop() {
            for entry in fs::read_dir(folder).unwrap() {
                let path = entry.unwrap().path();
                if path.is_dir() {
                    stack.push(path);
                } else {
                    found.push((
                        path.strip_prefix(root).unwrap().to_path_buf(),
                        fs::read(&path).unwrap(),
                    ));
                }
            }
        }
        found.sort();
        found
    }

    #[test]
    fn copies_verifies_and_keeps_times_without_changing_the_source() {
        let root = folder("copy");
        let source = data(&root);
        let before = snapshot(&source);
        let destination = root.join("elsewhere/new data");
        let planned: serde_json::Value =
            serde_json::from_slice(&plan(&request(&source, &destination)).unwrap()).unwrap();
        assert_eq!(planned["blocker"], "");
        assert_eq!(planned["files"], 3);
        assert_eq!(planned["bytes"], 300_000 + 18 + 3);
        assert!(!destination.exists(), "planning creates nothing");
        let moved: serde_json::Value = serde_json::from_slice(
            &relocate(&request(&source, &destination), None, std::ptr::null_mut()).unwrap(),
        )
        .unwrap();
        assert_eq!(moved["files"], 3);
        assert_eq!(snapshot(&source), before, "the source is never changed");
        let copied = snapshot(&destination);
        assert_eq!(copied.len(), 3);
        assert!(!destination.join("temp").exists() && !destination.join("library").exists());
        let weights = "models/sd15-baseline/unet/weights.bin";
        assert_eq!(
            fs::metadata(destination.join(weights))
                .unwrap()
                .modified()
                .unwrap(),
            fs::metadata(source.join(weights))
                .unwrap()
                .modified()
                .unwrap()
        );
        assert!(!root.join("elsewhere/.new data.moving").exists());
        let again: serde_json::Value =
            serde_json::from_slice(&plan(&request(&source, &destination)).unwrap()).unwrap();
        assert!(
            again["blocker"]
                .as_str()
                .unwrap()
                .contains("already has files")
        );
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn removing_the_old_copy_keeps_files_that_differ_or_were_never_copied() {
        let root = folder("remove");
        let source = data(&root);
        let destination = root.join("new");
        relocate(&request(&source, &destination), None, std::ptr::null_mut()).unwrap();
        // Changed after the move in the new folder, and added to the old one after the move.
        write(
            &destination,
            "jobs/install-sd15.json",
            br#"{"status":"changed"}"#,
        );
        write(&source, "artifacts/late/image.png", b"late");
        let report: serde_json::Value = serde_json::from_slice(
            &remove_moved(&request(&source, &destination), None, std::ptr::null_mut()).unwrap(),
        )
        .unwrap();
        assert_eq!(report["removedFiles"], 2);
        assert_eq!(report["keptFiles"], 2);
        assert_eq!(report["folderRemoved"], false);
        assert!(source.join("jobs/install-sd15.json").exists());
        assert!(source.join("artifacts/late/image.png").exists());
        assert!(
            !source.join("models").exists()
                && !source.join("temp").exists()
                && !source.join("library").exists()
        );
        assert_eq!(
            snapshot(&destination).len(),
            3,
            "the new folder is untouched"
        );
        // Nested or identical folders are refused.
        assert_eq!(
            remove_moved(&request(&source, &source), None, std::ptr::null_mut()),
            Err(INVALID)
        );
        // Once only copied files remain, the old folder goes completely.
        fs::remove_file(source.join("artifacts/late/image.png")).unwrap();
        fs::copy(
            destination.join("jobs/install-sd15.json"),
            source.join("jobs/install-sd15.json"),
        )
        .unwrap();
        let modified = fs::metadata(destination.join("jobs/install-sd15.json"))
            .unwrap()
            .modified()
            .unwrap();
        File::options()
            .write(true)
            .open(source.join("jobs/install-sd15.json"))
            .unwrap()
            .set_modified(modified)
            .unwrap();
        let report: serde_json::Value = serde_json::from_slice(
            &remove_moved(&request(&source, &destination), None, std::ptr::null_mut()).unwrap(),
        )
        .unwrap();
        assert_eq!(report["folderRemoved"], true);
        assert!(!source.exists());
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn refuses_nested_busy_and_linked_folders_and_cleans_up_a_cancelled_copy() {
        let root = folder("refuse");
        let source = data(&root);
        let blocker = |destination: &Path| -> String {
            let planned: serde_json::Value =
                serde_json::from_slice(&plan(&request(&source, destination)).unwrap()).unwrap();
            planned["blocker"].as_str().unwrap().to_owned()
        };
        assert!(blocker(&source.join("inner")).contains("outside"));
        assert!(blocker(&root).contains("does not contain"));
        assert!(blocker(&source).contains("already the data folder"));
        // Cancelling part-way leaves neither a destination nor a staging folder.
        let destination = root.join("cancelled");
        unsafe extern "C" fn stop(completed: u64, _total: u64, _context: *mut c_void) -> i32 {
            i32::from(completed > 100_000)
        }
        assert_eq!(
            relocate(
                &request(&source, &destination),
                Some(stop),
                std::ptr::null_mut()
            ),
            Err(CANCELLED)
        );
        assert!(!destination.exists() && !root.join(".cancelled.moving").exists());
        // An interrupted move's marked staging folder is reported, then removed by abandon.
        write(&root, ".crashed.moving/.media-workbench-move", b"");
        write(&root, ".crashed.moving/models/half.bin", b"half");
        let crashed = root.join("crashed");
        let planned: serde_json::Value =
            serde_json::from_slice(&plan(&request(&source, &crashed)).unwrap()).unwrap();
        assert_eq!(planned["leftover"], true);
        let abandon = serde_json::to_vec(
            &serde_json::json!({ "source": source, "destination": crashed, "abandon": true }),
        )
        .unwrap();
        relocate(&abandon, None, std::ptr::null_mut()).unwrap();
        assert!(!root.join(".crashed.moving").exists());
        // A folder with the staging name but without the marker is not ours and is kept.
        write(&root, ".mine.moving/keep.txt", b"user");
        let mine = serde_json::to_vec(
            &serde_json::json!({ "source": source, "destination": root.join("mine"), "abandon": true }),
        )
        .unwrap();
        assert_eq!(relocate(&mine, None, std::ptr::null_mut()), Err(INVALID));
        assert!(root.join(".mine.moving/keep.txt").exists());
        // Another owner holding the data folder blocks the move.
        let held = Lock::acquire(&source).unwrap();
        assert_eq!(
            relocate(
                &request(&source, &root.join("busy")),
                None,
                std::ptr::null_mut()
            ),
            Err(crate::tidy::BUSY)
        );
        drop(held);
        fs::remove_dir_all(&root).unwrap();
    }
}
