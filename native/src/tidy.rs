//! Automatic, bounded clean-up of application-made clutter in the data folder.
//!
//! Runs only while holding the data-folder lock that setup and generation hold, so it never races
//! them. It removes old logs, old failed/cancelled/interrupted job records beyond the ones Library
//! lists, stale setup downloads, and (only once setup is verified) the download caches. It never
//! removes images, listed job records, private inputs those records need, or files it did not make.
use crate::{
    INVALID, IO_ERROR,
    path_policy::{is_link, no_links},
    storage::{Cancel, cancelled, lexically_valid_root},
    valid_relative_path,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    ffi::c_void,
    fs::{self, File, Metadata, OpenOptions},
    io::ErrorKind,
    path::{Path, PathBuf},
    time::{Duration, SystemTime},
};

pub(crate) const OUTPUT_CAPACITY: usize = 1024;
/// Another owner (setup, generation, model import, or removal) holds the data-folder lock.
pub(crate) const BUSY: i32 = 7;
/// The newest logs kept; older ones go, as do logs older than `LOG_MAX_AGE_DAYS`.
const KEEP_LOGS: usize = 300;
const LOG_MAX_AGE_DAYS: u64 = 30;
/// Library lists this many failed, cancelled, and interrupted jobs; older ones are removed.
const KEEP_PROBLEM_JOBS: usize = 50;
/// Unfinished setup downloads older than this are removed; all of them once setup is verified.
const PARTIAL_MAX_AGE_HOURS: u64 = 72;
const PROBLEM_STATUSES: [&str; 3] = ["failed", "cancelled", "interrupted"];
const MAX_RECORD_BYTES: u64 = 1_048_576;
/// Caches are renamed here first, so a crash mid-removal never leaves a half-deleted cache in use.
const DISCARD: &str = ".removing";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    data_root: PathBuf,
    runtime_profile_id: String,
    model_ids: Vec<String>,
    installation_verified: bool,
    remove_download_caches: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Policy {
    logs: usize,
    log_days: u64,
    problem_jobs: usize,
    partial_hours: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    removed_files: u64,
    removed_bytes: u64,
    logs: u64,
    job_records: u64,
    partial_downloads: u64,
    download_caches: bool,
    /// Files that could not be removed now (for example, open in another program); tried again next time.
    skipped: u64,
    policy: Policy,
}

impl Report {
    fn new() -> Self {
        Self {
            removed_files: 0,
            removed_bytes: 0,
            logs: 0,
            job_records: 0,
            partial_downloads: 0,
            download_caches: false,
            skipped: 0,
            policy: Policy {
                logs: KEEP_LOGS,
                log_days: LOG_MAX_AGE_DAYS,
                problem_jobs: KEEP_PROBLEM_JOBS,
                partial_hours: PARTIAL_MAX_AGE_HOURS,
            },
        }
    }

    /// Removes one regular file; true when it is gone because of this call.
    fn remove_file(&mut self, path: &Path, bytes: u64) -> bool {
        match delete_file(path) {
            Ok(()) => {
                self.removed_files += 1;
                self.removed_bytes = self.removed_bytes.saturating_add(bytes);
                true
            }
            Err(error) if error.kind() == ErrorKind::NotFound => false,
            Err(_) => {
                self.skipped += 1;
                false
            }
        }
    }
}

fn delete_file(path: &Path) -> std::io::Result<()> {
    match fs::remove_file(path) {
        #[cfg(windows)]
        Err(error) if error.kind() == ErrorKind::PermissionDenied => {
            // Package caches can contain read-only files; Windows refuses to delete those.
            let mut permissions = fs::symlink_metadata(path)?.permissions();
            if !permissions.readonly() {
                return Err(error);
            }
            #[allow(clippy::permissions_set_readonly_false)]
            // Windows only: clears FILE_ATTRIBUTE_READONLY.
            permissions.set_readonly(false);
            fs::set_permissions(path, permissions)?;
            fs::remove_file(path)
        }
        result => result,
    }
}

/// The data-folder lock: byte 0 of `.operation.lock`, the same range the Python worker locks.
struct Lock {
    #[cfg_attr(not(windows), allow(dead_code))]
    file: File,
}

#[cfg(windows)]
#[repr(C)]
struct Overlapped {
    internal: usize,
    internal_high: usize,
    offset: u32,
    offset_high: u32,
    event: *mut c_void,
}

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    #[link_name = "LockFileEx"]
    fn lock_file_ex(
        file: *mut c_void,
        flags: u32,
        reserved: u32,
        low: u32,
        high: u32,
        overlapped: *mut Overlapped,
    ) -> i32;
    #[link_name = "UnlockFileEx"]
    fn unlock_file_ex(
        file: *mut c_void,
        reserved: u32,
        low: u32,
        high: u32,
        overlapped: *mut Overlapped,
    ) -> i32;
}

#[cfg(windows)]
fn region() -> Overlapped {
    Overlapped {
        internal: 0,
        internal_high: 0,
        offset: 0,
        offset_high: 0,
        event: std::ptr::null_mut(),
    }
}

impl Lock {
    fn acquire(root: &Path) -> Result<Self, i32> {
        let path = root.join(".operation.lock");
        no_links(&path)?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&path)
            .map_err(|_| IO_ERROR)?;
        #[cfg(windows)]
        {
            use std::os::windows::io::AsRawHandle;
            const EXCLUSIVE: u32 = 2;
            const FAIL_IMMEDIATELY: u32 = 1;
            let mut overlapped = region();
            // SAFETY: the handle is open for this call; overlapped is live and describes byte 0.
            if unsafe {
                lock_file_ex(
                    file.as_raw_handle(),
                    EXCLUSIVE | FAIL_IMMEDIATELY,
                    0,
                    1,
                    0,
                    &mut overlapped,
                )
            } == 0
            {
                return Err(BUSY);
            }
            Ok(Self { file })
        }
        #[cfg(not(windows))]
        {
            drop(file);
            Err(IO_ERROR) // This application/service targets Windows x64.
        }
    }
}

#[cfg(windows)]
impl Drop for Lock {
    fn drop(&mut self) {
        use std::os::windows::io::AsRawHandle;
        let mut overlapped = region();
        // SAFETY: the handle is still open; this releases the byte range locked in acquire.
        unsafe { unlock_file_ex(self.file.as_raw_handle(), 0, 1, 0, &mut overlapped) };
    }
}

fn valid_job_id(value: &str) -> bool {
    value.len() == 32
        && value
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

fn age(metadata: &Metadata, now: SystemTime) -> Duration {
    metadata
        .modified()
        .ok()
        .and_then(|modified| now.duration_since(modified).ok())
        .unwrap_or(Duration::ZERO)
}

/// Regular files and folders directly inside `folder`, never following links. Missing folders are empty.
fn entries(folder: &Path) -> Result<Vec<(PathBuf, String, Metadata)>, i32> {
    let listing = match fs::read_dir(folder) {
        Ok(value) => value,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(Vec::new()),
        Err(_) => return Err(IO_ERROR),
    };
    let mut result = Vec::new();
    for entry in listing {
        let entry = match entry {
            Ok(value) => value,
            Err(error) if error.kind() == ErrorKind::NotFound => continue,
            Err(_) => return Err(IO_ERROR),
        };
        // Does not follow links; Windows reuses metadata already returned by enumeration.
        let metadata = match entry.metadata() {
            Ok(value) => value,
            Err(error) if error.kind() == ErrorKind::NotFound => continue,
            Err(_) => return Err(IO_ERROR),
        };
        if is_link(&metadata) {
            continue;
        }
        let Some(name) = entry.file_name().to_str().map(str::to_owned) else {
            continue; // Names the application never makes.
        };
        result.push((entry.path(), name, metadata));
    }
    Ok(result)
}

/// Removes a folder tree without following links: a link or junction inside is removed, not its target.
fn remove_tree(
    path: &Path,
    report: &mut Report,
    cancel: Cancel,
    context: *mut c_void,
) -> Result<(), i32> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if is_link(&metadata) || !metadata.is_dir() => return Err(INVALID),
        Ok(_) => {}
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(_) => return Err(IO_ERROR),
    }
    let mut stack = vec![(path.to_path_buf(), false)];
    while let Some((folder, emptied)) = stack.pop() {
        cancelled(cancel, context)?;
        if emptied {
            match fs::remove_dir(&folder) {
                Ok(()) => {}
                Err(error) if error.kind() == ErrorKind::NotFound => {}
                Err(_) => report.skipped += 1,
            }
            continue;
        }
        stack.push((folder.clone(), true));
        let listing = match fs::read_dir(&folder) {
            Ok(value) => value,
            Err(error) if error.kind() == ErrorKind::NotFound => continue,
            Err(_) => {
                report.skipped += 1;
                continue;
            }
        };
        for entry in listing {
            cancelled(cancel, context)?;
            let Ok(entry) = entry else {
                report.skipped += 1;
                continue;
            };
            let Ok(metadata) = entry.metadata() else {
                report.skipped += 1;
                continue;
            };
            let child = entry.path();
            if is_link(&metadata) {
                // RemoveDirectoryW deletes a junction or directory link itself; files use DeleteFileW.
                if fs::remove_dir(&child).is_err() && fs::remove_file(&child).is_err() {
                    report.skipped += 1;
                }
            } else if metadata.is_dir() {
                stack.push((child, false));
            } else {
                report.remove_file(&child, metadata.len());
            }
        }
    }
    Ok(())
}

struct Job {
    id: String,
    status: String,
    modified: SystemTime,
}

/// Job records named `<32 hex>.json` whose own `id` and `status` can be read. Others are left alone.
fn jobs(root: &Path, cancel: Cancel, context: *mut c_void) -> Result<Vec<Job>, i32> {
    let mut result = Vec::new();
    for (path, name, metadata) in entries(&root.join("jobs"))? {
        cancelled(cancel, context)?;
        let Some(id) = name.strip_suffix(".json") else {
            continue;
        };
        if !valid_job_id(id) || !metadata.is_file() || metadata.len() > MAX_RECORD_BYTES {
            continue;
        }
        let Ok(bytes) = fs::read(&path) else {
            continue;
        };
        let Ok(record) = serde_json::from_slice::<serde_json::Value>(&bytes) else {
            continue; // A damaged record is never removed automatically.
        };
        let (Some(recorded), Some(status)) = (record["id"].as_str(), record["status"].as_str())
        else {
            continue;
        };
        if recorded != id {
            continue;
        }
        result.push(Job {
            id: id.to_owned(),
            status: status.to_owned(),
            modified: metadata.modified().unwrap_or(SystemTime::UNIX_EPOCH),
        });
    }
    Ok(result)
}

/// The job's private input copies and any half-written image; never its finished image.
fn remove_job_files(root: &Path, id: &str, keep_inputs: bool, report: &mut Report) {
    let folder = root.join("artifacts").join(id);
    match fs::symlink_metadata(&folder) {
        Ok(metadata) if !is_link(&metadata) && metadata.is_dir() => {}
        _ => return,
    }
    let finished = folder.join("image.png").exists();
    let Ok(listing) = entries(&folder) else {
        report.skipped += 1;
        return;
    };
    for (path, name, metadata) in listing {
        let input = matches!(
            name.as_str(),
            "mask.png" | "source.png" | "source.jpg" | "source.jpeg" | "source.webp" | "source.bmp"
        );
        if metadata.is_file() && (name == "image.partial" || (input && !keep_inputs && !finished)) {
            report.remove_file(&path, metadata.len());
        }
    }
    if !keep_inputs {
        let _ = fs::remove_dir(&folder); // Only when empty.
    }
}

fn tidy_jobs(
    root: &Path,
    protected: &mut HashSet<String>,
    report: &mut Report,
    cancel: Cancel,
    context: *mut c_void,
) -> Result<(), i32> {
    let mut problems = Vec::new();
    for job in jobs(root, cancel, context)? {
        if PROBLEM_STATUSES.contains(&job.status.as_str()) {
            problems.push(job);
        } else if job.status == "running" {
            protected.insert(job.id); // An owner may still be writing it; recovery decides.
        }
    }
    // Library lists the newest first (modification time, then name); keep exactly those.
    problems.sort_by(|a, b| (b.modified, &b.id).cmp(&(a.modified, &a.id)));
    for (index, job) in problems.into_iter().enumerate() {
        cancelled(cancel, context)?;
        if index < KEEP_PROBLEM_JOBS {
            remove_job_files(root, &job.id, true, report);
            protected.insert(job.id);
            continue;
        }
        // Inputs and log first, record last: an interruption leaves a record, never orphaned files.
        remove_job_files(root, &job.id, false, report);
        for kind in ["generate", "upscale"] {
            let log = root.join("logs").join(format!("{kind}-{}.log", job.id));
            if let Ok(metadata) = fs::symlink_metadata(&log)
                && metadata.is_file()
                && !is_link(&metadata)
                && report.remove_file(&log, metadata.len())
            {
                report.logs += 1;
            }
        }
        let record = root.join("jobs").join(format!("{}.json", job.id));
        if let Ok(metadata) = fs::symlink_metadata(&record)
            && report.remove_file(&record, metadata.len())
        {
            report.job_records += 1;
        }
    }
    Ok(())
}

fn tidy_logs(
    root: &Path,
    protected: &HashSet<String>,
    now: SystemTime,
    report: &mut Report,
    cancel: Cancel,
    context: *mut c_void,
) -> Result<(), i32> {
    let mut logs: Vec<_> = entries(&root.join("logs"))?
        .into_iter()
        .filter(|(_, name, metadata)| {
            metadata.is_file() && name.to_ascii_lowercase().ends_with(".log")
        })
        .filter(|(_, name, _)| {
            // `<operation>-<job id>.log`: an unfinished job's log stays while Library lists it.
            let stem = &name[..name.len() - 4];
            !stem
                .rsplit_once('-')
                .is_some_and(|(_, id)| protected.contains(id))
        })
        .collect();
    logs.sort_by(|a, b| {
        let (left, right) = (a.2.modified().ok(), b.2.modified().ok());
        (right, &b.1).cmp(&(left, &a.1))
    });
    let limit = Duration::from_secs(LOG_MAX_AGE_DAYS * 24 * 3600);
    for (index, (path, _, metadata)) in logs.into_iter().enumerate() {
        cancelled(cancel, context)?;
        if (index >= KEEP_LOGS || age(&metadata, now) > limit)
            && report.remove_file(&path, metadata.len())
        {
            report.logs += 1;
        }
    }
    Ok(())
}

/// `.partial` (Media Workbench) and `.incomplete` (Hugging Face) downloads inside setup folders.
fn tidy_partials(
    root: &Path,
    folders: &[String],
    all: bool,
    now: SystemTime,
    report: &mut Report,
    cancel: Cancel,
    context: *mut c_void,
) -> Result<(), i32> {
    let limit = Duration::from_secs(PARTIAL_MAX_AGE_HOURS * 3600);
    for folder in folders {
        let start = root.join(folder);
        no_links(&start)?;
        let mut stack = vec![start];
        while let Some(current) = stack.pop() {
            for (path, name, metadata) in entries(&current)? {
                cancelled(cancel, context)?;
                if metadata.is_dir() {
                    stack.push(path);
                } else if metadata.is_file()
                    && (name.ends_with(".partial") || name.ends_with(".incomplete"))
                    && (all || age(&metadata, now) > limit)
                    && report.remove_file(&path, metadata.len())
                {
                    report.partial_downloads += 1;
                }
            }
        }
    }
    Ok(())
}

fn tidy_caches(
    root: &Path,
    report: &mut Report,
    cancel: Cancel,
    context: *mut c_void,
) -> Result<(), i32> {
    let discard = root.join(DISCARD);
    let stamp = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    for name in ["cache", "bootstrap"] {
        cancelled(cancel, context)?;
        let path = root.join(name);
        match fs::symlink_metadata(&path) {
            Ok(metadata) if is_link(&metadata) => return Err(INVALID),
            Ok(metadata) if metadata.is_dir() => {}
            Ok(_) => continue,
            Err(error) if error.kind() == ErrorKind::NotFound => continue,
            Err(_) => return Err(IO_ERROR),
        }
        fs::create_dir_all(&discard).map_err(|_| IO_ERROR)?;
        if fs::rename(&path, discard.join(format!("{name}-{stamp}"))).is_err() {
            report.skipped += 1; // Something holds a file open; the next clean-up tries again.
        }
    }
    remove_tree(&discard, report, cancel, context)?;
    report.download_caches = !root.join("cache").exists() && !root.join("bootstrap").exists();
    Ok(())
}

pub(crate) fn tidy(input: &[u8], cancel: Cancel, context: *mut c_void) -> Result<Vec<u8>, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    let valid_id = |id: &str| id.len() <= 128 && !id.contains('/') && valid_relative_path(id);
    if !lexically_valid_root(&request.data_root)
        || !valid_id(&request.runtime_profile_id)
        || request.model_ids.is_empty()
        || request.model_ids.len() > 32
        || request
            .model_ids
            .iter()
            .any(|id| !valid_id(id) || id == "imported")
        || (request.remove_download_caches && !request.installation_verified)
    {
        return Err(INVALID);
    }
    cancelled(cancel, context)?;
    let mut report = Report::new();
    no_links(&request.data_root)?;
    let root = match fs::canonicalize(&request.data_root) {
        Ok(value) => value,
        Err(error) if error.kind() == ErrorKind::NotFound => {
            return serde_json::to_vec(&report).map_err(|_| crate::INTERNAL); // Nothing made yet.
        }
        Err(_) => return Err(IO_ERROR),
    };
    no_links(&root)?;
    if root.parent().is_none() || !root.is_dir() {
        return Err(INVALID);
    }
    for area in ["jobs", "logs", "artifacts", DISCARD] {
        no_links(&root.join(area))?;
    }
    let _lock = Lock::acquire(&root)?;
    let now = SystemTime::now();
    // An earlier clean-up stopped part-way (crash, power loss, or cancellation).
    remove_tree(&root.join(DISCARD), &mut report, cancel, context)?;
    let mut protected = HashSet::new();
    tidy_jobs(&root, &mut protected, &mut report, cancel, context)?;
    tidy_logs(&root, &protected, now, &mut report, cancel, context)?;
    let mut folders = vec!["bootstrap".to_owned()];
    folders.extend(request.model_ids.iter().map(|id| format!("models/{id}")));
    tidy_partials(
        &root,
        &folders,
        request.installation_verified,
        now,
        &mut report,
        cancel,
        context,
    )?;
    if request.remove_download_caches {
        tidy_caches(&root, &mut report, cancel, context)?;
    }
    serde_json::to_vec(&report).map_err(|_| crate::INTERNAL)
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    use std::time::Duration;

    const DAY: u64 = 24 * 3600;

    fn folder(name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "media-workbench-tidy-{name}-{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        fs::canonicalize(path).unwrap()
    }

    fn write(root: &Path, relative: &str, body: &[u8], age_seconds: u64) {
        let path = root.join(relative);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, body).unwrap();
        File::options()
            .write(true)
            .open(&path)
            .unwrap()
            .set_modified(SystemTime::now() - Duration::from_secs(age_seconds))
            .unwrap();
    }

    fn record(root: &Path, id: &str, status: &str, age_seconds: u64) {
        let body = format!(r#"{{"id":"{id}","status":"{status}"}}"#);
        write(
            root,
            &format!("jobs/{id}.json"),
            body.as_bytes(),
            age_seconds,
        );
    }

    fn id(number: usize) -> String {
        format!("{number:032x}")
    }

    fn run(root: &Path, verified: bool, caches: bool) -> Result<serde_json::Value, i32> {
        let input = serde_json::to_vec(&serde_json::json!({
            "dataRoot": root, "runtimeProfileId": "sd15-cu126-v1",
            "modelIds": ["sd15-baseline", "realesrgan-x4plus"],
            "installationVerified": verified, "removeDownloadCaches": caches,
        }))
        .unwrap();
        tidy(&input, None, std::ptr::null_mut())
            .map(|bytes| serde_json::from_slice(&bytes).unwrap())
    }

    #[test]
    fn keeps_the_problem_jobs_library_lists_and_removes_older_ones_with_their_files() {
        let root = folder("jobs");
        for number in 0..53 {
            let job = id(number + 1);
            // Larger numbers are newer.
            record(&root, &job, "failed", 1000 - number as u64);
            write(&root, &format!("artifacts/{job}/source.png"), b"in", 0);
            write(&root, &format!("artifacts/{job}/mask.png"), b"in", 0);
            write(&root, &format!("logs/generate-{job}.log"), b"log", 0);
        }
        let (running, succeeded, finished_failure) = (id(100), id(101), id(102));
        record(&root, &running, "running", 90 * DAY);
        write(
            &root,
            &format!("logs/generate-{running}.log"),
            b"log",
            90 * DAY,
        );
        record(&root, &succeeded, "succeeded", 90 * DAY);
        write(
            &root,
            &format!("artifacts/{succeeded}/image.png"),
            b"png",
            90 * DAY,
        );
        write(
            &root,
            &format!("artifacts/{succeeded}/source.png"),
            b"in",
            90 * DAY,
        );
        record(&root, &finished_failure, "cancelled", 2000);
        write(
            &root,
            &format!("artifacts/{finished_failure}/image.png"),
            b"png",
            0,
        );
        write(
            &root,
            &format!("artifacts/{finished_failure}/source.jpg"),
            b"in",
            0,
        );
        write(
            &root,
            &format!("artifacts/{finished_failure}/image.partial"),
            b"half",
            0,
        );
        write(
            &root,
            &format!("jobs/{}.json", id(103)),
            b"{damaged",
            90 * DAY,
        );
        write(
            &root,
            "jobs/install-sd15.json",
            br#"{"status":"failed"}"#,
            90 * DAY,
        );
        write(
            &root,
            &format!("artifacts/{}/image.partial", id(53)),
            b"half",
            0,
        );

        let report = run(&root, false, false).unwrap();
        assert_eq!(report["jobRecords"], 4);
        assert_eq!(report["policy"]["problemJobs"], 50);
        // The 50 newest problems stay with their inputs and logs; a crash's half image goes.
        for number in 4..=53 {
            let job = id(number);
            assert!(root.join(format!("jobs/{job}.json")).is_file(), "{number}");
            assert!(root.join(format!("artifacts/{job}/source.png")).is_file());
            assert!(root.join(format!("logs/generate-{job}.log")).is_file());
        }
        assert!(
            !root
                .join(format!("artifacts/{}/image.partial", id(53)))
                .exists()
        );
        // The oldest problems go completely, including their private inputs and folders.
        for number in 1..=3 {
            let job = id(number);
            assert!(!root.join(format!("jobs/{job}.json")).exists());
            assert!(!root.join(format!("artifacts/{job}")).exists());
            assert!(!root.join(format!("logs/generate-{job}.log")).exists());
        }
        // A failure that still has a finished image keeps the image and its input.
        assert!(!root.join(format!("jobs/{finished_failure}.json")).exists());
        assert!(
            root.join(format!("artifacts/{finished_failure}/image.png"))
                .is_file()
        );
        assert!(
            root.join(format!("artifacts/{finished_failure}/source.jpg"))
                .is_file()
        );
        assert!(
            !root
                .join(format!("artifacts/{finished_failure}/image.partial"))
                .exists()
        );
        // Running, succeeded, damaged, and setup records are never removed.
        for kept in [
            format!("jobs/{running}.json"),
            format!("logs/generate-{running}.log"),
            format!("jobs/{succeeded}.json"),
            format!("artifacts/{succeeded}/image.png"),
            format!("artifacts/{succeeded}/source.png"),
            format!("jobs/{}.json", id(103)),
            "jobs/install-sd15.json".into(),
        ] {
            assert!(root.join(&kept).is_file(), "{kept}");
        }
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn bounds_logs_by_count_and_age_but_keeps_unfinished_jobs_logs() {
        let root = folder("logs");
        for number in 0..305 {
            write(
                &root,
                &format!("logs/verify-{}.log", id(number + 1)),
                b"l",
                1000 - number as u64,
            );
        }
        let failed = id(400);
        record(&root, &failed, "interrupted", 40 * DAY);
        write(&root, &format!("logs/upscale-{failed}.log"), b"l", 40 * DAY);
        write(
            &root,
            &format!("logs/generate-{}.log", id(401)),
            b"l",
            40 * DAY,
        );
        write(&root, "logs/notes.txt", b"mine", 400 * DAY);
        write(&root, "logs/folder/old.log", b"l", 400 * DAY);

        let report = run(&root, false, false).unwrap();
        assert_eq!(report["logs"], 6);
        assert_eq!(report["removedFiles"], 6);
        for number in 1..=5 {
            assert!(
                !root
                    .join(format!("logs/verify-{}.log", id(number)))
                    .exists()
            );
        }
        for number in 6..=305 {
            assert!(
                root.join(format!("logs/verify-{}.log", id(number)))
                    .is_file()
            );
        }
        assert!(!root.join(format!("logs/generate-{}.log", id(401))).exists());
        assert!(root.join(format!("logs/upscale-{failed}.log")).is_file());
        assert!(root.join("logs/notes.txt").is_file());
        assert!(root.join("logs/folder/old.log").is_file());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn expires_stale_downloads_and_all_of_them_after_verification() {
        let root = folder("partials");
        write(&root, "bootstrap/python.zip.partial", b"old", 4 * DAY);
        write(&root, "bootstrap/uv.zip.partial", b"new", 60);
        write(&root, "bootstrap/python.zip", b"zip", 4 * DAY);
        let hub = "models/sd15-baseline/.cache/huggingface/download/unet";
        write(&root, &format!("{hub}/abc.incomplete"), b"new", 60);
        write(&root, &format!("{hub}/old.incomplete"), b"old", 5 * DAY);
        write(
            &root,
            "models/sd15-baseline/unet/weights.safetensors",
            b"w",
            9 * DAY,
        );
        write(
            &root,
            "models/realesrgan-x4plus/weights.pth.partial",
            b"new",
            60,
        );
        write(
            &root,
            &format!("models/imported/{}/model.partial", id(9)),
            b"x",
            9 * DAY,
        );
        write(&root, "artifacts/picture.partial", b"x", 9 * DAY);

        let report = run(&root, false, false).unwrap();
        assert_eq!(report["partialDownloads"], 2);
        assert!(!root.join("bootstrap/python.zip.partial").exists());
        assert!(!root.join(format!("{hub}/old.incomplete")).exists());
        assert!(root.join("bootstrap/uv.zip.partial").is_file());
        assert!(root.join(format!("{hub}/abc.incomplete")).is_file());

        let report = run(&root, true, false).unwrap();
        assert_eq!(report["partialDownloads"], 3);
        assert!(!root.join("bootstrap/uv.zip.partial").exists());
        assert!(!root.join(format!("{hub}/abc.incomplete")).exists());
        assert!(
            !root
                .join("models/realesrgan-x4plus/weights.pth.partial")
                .exists()
        );
        for kept in [
            "bootstrap/python.zip".to_owned(),
            "models/sd15-baseline/unet/weights.safetensors".into(),
            format!("models/imported/{}/model.partial", id(9)),
            "artifacts/picture.partial".into(),
        ] {
            assert!(root.join(&kept).is_file(), "{kept}");
        }
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn removes_download_caches_only_after_verification_and_finishes_interrupted_removals() {
        let root = folder("caches");
        write(&root, "cache/uv/archive/torch/lib.dll", b"wheel", 0);
        write(&root, "cache/huggingface/xet/chunk", b"chunk", 0);
        write(&root, "bootstrap/uv-0.1/uv.exe", b"uv", 0);
        write(&root, "runtimes/sd15-cu126-v1/python/python.exe", b"py", 0);
        write(&root, &format!("artifacts/{}/image.png", id(1)), b"png", 0);
        write(&root, ".removing/cache-1/leftover.whl", b"half-removed", 0);
        let mut readonly = fs::metadata(root.join("cache/uv/archive/torch/lib.dll"))
            .unwrap()
            .permissions();
        readonly.set_readonly(true);
        fs::set_permissions(root.join("cache/uv/archive/torch/lib.dll"), readonly).unwrap();

        assert_eq!(run(&root, false, true).unwrap_err(), INVALID);
        assert!(root.join("cache/uv/archive/torch/lib.dll").is_file());

        let report = run(&root, false, false).unwrap();
        assert!(
            !root.join(DISCARD).exists(),
            "an earlier removal is finished"
        );
        assert_eq!(report["downloadCaches"], false);
        assert!(root.join("cache/huggingface/xet/chunk").is_file());

        let report = run(&root, true, true).unwrap();
        assert_eq!(report["downloadCaches"], true);
        assert_eq!(report["skipped"], 0);
        for gone in ["cache", "bootstrap", DISCARD] {
            assert!(!root.join(gone).exists(), "{gone}");
        }
        assert!(
            root.join("runtimes/sd15-cu126-v1/python/python.exe")
                .is_file()
        );
        assert!(
            root.join(format!("artifacts/{}/image.png", id(1)))
                .is_file()
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn waits_for_the_data_folder_owner_and_changes_nothing() {
        let root = folder("busy");
        write(&root, "bootstrap/python.zip.partial", b"old", 9 * DAY);
        write(&root, "logs/verify-old.log", b"l", 90 * DAY);
        let owner = Lock::acquire(&root).unwrap();
        assert_eq!(run(&root, true, true).unwrap_err(), BUSY);
        assert!(root.join("bootstrap/python.zip.partial").is_file());
        assert!(root.join("logs/verify-old.log").is_file());
        drop(owner);
        assert_eq!(run(&root, true, true).unwrap()["logs"], 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn invalid_requests_and_missing_folders_touch_nothing() {
        for input in [
            br#"{}"#.as_slice(),
            br#"{"dataRoot":"relative","runtimeProfileId":"p","modelIds":["m"],"installationVerified":false,"removeDownloadCaches":false}"#,
            br#"{"dataRoot":"C:/unused","runtimeProfileId":"p","modelIds":["../m"],"installationVerified":false,"removeDownloadCaches":false}"#,
            br#"{"dataRoot":"C:/unused","runtimeProfileId":"p","modelIds":["imported"],"installationVerified":true,"removeDownloadCaches":false}"#,
        ] {
            assert_eq!(tidy(input, None, std::ptr::null_mut()), Err(INVALID));
        }
        let missing = std::env::temp_dir().join("media-workbench-tidy-missing-folder");
        let _ = fs::remove_dir_all(&missing);
        assert_eq!(run(&missing, true, true).unwrap()["removedFiles"], 0);
        assert!(!missing.exists());
    }
}
