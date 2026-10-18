//! Free space and drive type for the data folder, without creating or reading any files.
use crate::{INVALID, IO_ERROR, storage::lexically_valid_root};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub(crate) const OUTPUT_CAPACITY: usize = 256;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    data_root: PathBuf,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Drive {
    free_bytes: u64,
    total_bytes: u64,
    drive_kind: &'static str,
}

#[cfg(windows)]
fn wide(path: &Path, trailing_separator: bool) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    let mut value: Vec<u16> = path.as_os_str().encode_wide().collect();
    if trailing_separator && value.last() != Some(&(b'\\' as u16)) {
        value.push(b'\\' as u16);
    }
    value.push(0);
    value
}

#[cfg(windows)]
pub(crate) fn space(root: &Path) -> Result<(u64, u64), i32> {
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
    let path = wide(root, true);
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
pub(crate) fn space(_root: &Path) -> Result<(u64, u64), i32> {
    Err(IO_ERROR) // This application/service targets Windows x64.
}

/// "ssd", "hdd", "usb", "removable", "network", or "unknown" for the volume holding an existing folder.
/// The same vocabulary as the worker's hardware profile.
#[cfg(windows)]
pub(crate) fn kind(root: &Path) -> &'static str {
    use std::ffi::c_void;
    const DRIVE_REMOTE: u32 = 4;
    const IOCTL_STORAGE_QUERY_PROPERTY: u32 = 0x002D_1400;
    const STORAGE_DEVICE_PROPERTY: u32 = 0;
    const STORAGE_DEVICE_SEEK_PENALTY_PROPERTY: u32 = 7;
    const BUS_TYPE_USB: u32 = 7;
    const DRIVE_REMOVABLE: u32 = 2;
    const OPEN_EXISTING: u32 = 3;
    const SHARE_READ_WRITE: u32 = 3;
    #[repr(C)]
    struct PropertyQuery {
        property_id: u32,
        query_type: u32,
        additional: [u8; 4],
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        #[link_name = "GetVolumePathNameW"]
        fn get_volume_path_name(path: *const u16, volume: *mut u16, length: u32) -> i32;
        #[link_name = "GetDriveTypeW"]
        fn get_drive_type(root: *const u16) -> u32;
        #[link_name = "GetVolumeNameForVolumeMountPointW"]
        fn get_volume_name(mount: *const u16, name: *mut u16, length: u32) -> i32;
        #[link_name = "CreateFileW"]
        fn create_file(
            name: *const u16,
            access: u32,
            share: u32,
            security: *mut c_void,
            disposition: u32,
            flags: u32,
            template: *mut c_void,
        ) -> *mut c_void;
        #[link_name = "DeviceIoControl"]
        fn device_io_control(
            device: *mut c_void,
            code: u32,
            input: *const c_void,
            input_size: u32,
            output: *mut c_void,
            output_size: u32,
            returned: *mut u32,
            overlapped: *mut c_void,
        ) -> i32;
        #[link_name = "CloseHandle"]
        fn close_handle(handle: *mut c_void) -> i32;
    }
    let path = wide(root, false);
    let mut volume = [0u16; 1024];
    // SAFETY: path is NUL-terminated; volume is a writable buffer of the declared length.
    if unsafe { get_volume_path_name(path.as_ptr(), volume.as_mut_ptr(), volume.len() as u32) } == 0
    {
        return "unknown";
    }
    // SAFETY: volume was NUL-terminated by the successful call above.
    match unsafe { get_drive_type(volume.as_ptr()) } {
        DRIVE_REMOTE => return "network",
        DRIVE_REMOVABLE => return "removable",
        _ => {}
    }
    let mut name = [0u16; 64];
    // SAFETY: volume is a NUL-terminated mount point; name is writable for its declared length.
    if unsafe { get_volume_name(volume.as_ptr(), name.as_mut_ptr(), name.len() as u32) } == 0 {
        return "unknown";
    }
    // "\\?\Volume{GUID}\" opens the directory; without the separator it opens the volume device.
    let end = name.iter().position(|c| *c == 0).unwrap_or(0);
    if end == 0 || name[end - 1] != b'\\' as u16 {
        return "unknown";
    }
    name[end - 1] = 0;
    // SAFETY: name is NUL-terminated. Zero access needs no administrator rights for this query.
    let device = unsafe {
        create_file(
            name.as_ptr(),
            0,
            SHARE_READ_WRITE,
            std::ptr::null_mut(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        )
    };
    if device.is_null() || device as isize == -1 {
        return "unknown";
    }
    let query = |property_id: u32, answer: &mut [u8]| -> usize {
        let request = PropertyQuery {
            property_id,
            query_type: 0, // PropertyStandardQuery
            additional: [0; 4],
        };
        let mut returned = 0u32;
        // SAFETY: device is an open handle; request and answer are live and sized as declared.
        let ok = unsafe {
            device_io_control(
                device,
                IOCTL_STORAGE_QUERY_PROPERTY,
                (&request as *const PropertyQuery).cast(),
                size_of::<PropertyQuery>() as u32,
                answer.as_mut_ptr().cast(),
                answer.len() as u32,
                &mut returned,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 { 0 } else { returned as usize }
    };
    // STORAGE_DEVICE_DESCRIPTOR: BusType is the u32 at offset 28; 7 is USB.
    let mut device_descriptor = [0u8; 1024];
    let usb = query(STORAGE_DEVICE_PROPERTY, &mut device_descriptor) >= 32
        && u32::from_le_bytes(device_descriptor[28..32].try_into().unwrap_or_default())
            == BUS_TYPE_USB;
    // DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty is the byte at offset 8.
    let mut penalty = [0u8; 12];
    let answered = query(STORAGE_DEVICE_SEEK_PENALTY_PROPERTY, &mut penalty) >= 9;
    // SAFETY: device was opened above and is closed exactly once.
    unsafe { close_handle(device) };
    if usb {
        "usb"
    } else if !answered {
        "unknown" // Volumes spanning several disks (Storage Spaces, RAID) may not answer.
    } else if penalty[8] != 0 {
        "hdd"
    } else {
        "ssd"
    }
}

#[cfg(not(windows))]
pub(crate) fn kind(_root: &Path) -> &'static str {
    "unknown"
}

/// Free space and drive type for a data folder that may not exist yet; nothing is created.
pub(crate) fn info(input: &[u8]) -> Result<Vec<u8>, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    if !lexically_valid_root(&request.data_root) {
        return Err(INVALID);
    }
    // The nearest existing folder is on the same volume unless a mount point is created later.
    let existing = request
        .data_root
        .ancestors()
        .find(|path| path.is_dir())
        .ok_or(IO_ERROR)?;
    let (free_bytes, total_bytes) = space(existing)?;
    serde_json::to_vec(&Drive {
        free_bytes,
        total_bytes,
        drive_kind: kind(existing),
    })
    .map_err(|_| crate::INTERNAL)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_roots_are_refused() {
        for input in [
            br#"{}"#.as_slice(),
            br#"{"dataRoot":"relative"}"#,
            br#"{"dataRoot":"C:/a/../b"}"#,
        ] {
            assert_eq!(info(input), Err(INVALID));
        }
    }
    #[cfg(windows)]
    #[test]
    fn reports_space_and_a_known_kind_for_a_missing_folder() {
        let missing = std::env::temp_dir().join("media-workbench-drive-check/not/created");
        let input = serde_json::to_vec(&serde_json::json!({ "dataRoot": missing })).unwrap();
        let result: serde_json::Value = serde_json::from_slice(&info(&input).unwrap()).unwrap();
        assert!(result["freeBytes"].as_u64().unwrap() > 0);
        assert!(result["totalBytes"].as_u64().unwrap() >= result["freeBytes"].as_u64().unwrap());
        assert!(
            ["ssd", "hdd", "usb", "removable", "network", "unknown"]
                .contains(&result["driveKind"].as_str().unwrap())
        );
        assert!(!missing.exists());
    }
}
