//! Shared checks for application-managed filesystem paths.
use crate::{INVALID, IO_ERROR};
use std::{fs, path::Path};

pub(crate) fn is_link(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return true; // Includes junctions and other reparse points.
        }
    }
    metadata.is_symlink()
}

pub(crate) fn no_links(path: &Path) -> Result<(), i32> {
    for ancestor in path.ancestors() {
        match fs::symlink_metadata(ancestor) {
            Ok(metadata) if is_link(&metadata) => return Err(INVALID),
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => return Err(IO_ERROR),
        }
    }
    Ok(())
}
