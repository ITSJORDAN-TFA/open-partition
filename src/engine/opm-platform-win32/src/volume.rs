use std::thread::sleep;
use std::time::Duration;

use opm_core::IoError;
use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
use windows_sys::Win32::Storage::FileSystem::{FindFirstVolumeW, FindNextVolumeW, FindVolumeClose};

use crate::handle::{Handle, GENERIC_READ, GENERIC_WRITE};
use crate::PlatformError;

const IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS: u32 = 0x0056_0000;
const FSCTL_LOCK_VOLUME: u32 = 0x0009_0018;
const FSCTL_DISMOUNT_VOLUME: u32 = 0x0009_0020;

/// Volume GUID path (`\\?\Volume{...}\`) of the volume that is exactly the partition starting
/// at `offset` on `disk`, if one exists.
pub fn find_volume(disk: u32, offset: u64) -> Result<Option<String>, IoError> {
    let mut name = [0u16; 512];
    #[allow(unsafe_code)]
    // SAFETY: `name` is writable for the length given.
    let find = unsafe { FindFirstVolumeW(name.as_mut_ptr(), name.len() as u32) };
    if find == INVALID_HANDLE_VALUE {
        return Err(crate::handle::last_error());
    }
    let mut found = None;
    loop {
        let len = name.iter().position(|&c| c == 0).unwrap_or(name.len());
        let path = String::from_utf16_lossy(&name[..len]);
        if volume_is_at(&path, disk, offset) {
            found = Some(path);
            break;
        }
        #[allow(unsafe_code)]
        // SAFETY: `find` is a live search handle and `name` is writable.
        let more = unsafe { FindNextVolumeW(find, name.as_mut_ptr(), name.len() as u32) };
        if more == 0 {
            break;
        }
    }
    #[allow(unsafe_code)]
    // SAFETY: closes the search handle opened above, once.
    unsafe {
        FindVolumeClose(find);
    }
    Ok(found)
}

fn volume_is_at(path: &str, disk: u32, offset: u64) -> bool {
    let Ok(handle) = Handle::open(path.trim_end_matches('\\'), 0, 0) else {
        return false;
    };
    let Ok(extents) = handle.ioctl_vec(IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, &[], 256) else {
        return false;
    };
    // VOLUME_DISK_EXTENTS: count, pad, then { DiskNumber, pad, StartingOffset, ExtentLength }.
    if extents.len() < 32 || u32::from_le_bytes(extents[0..4].try_into().unwrap_or_default()) != 1 {
        return false;
    }
    let extent_disk = u32::from_le_bytes(extents[8..12].try_into().unwrap_or_default());
    let extent_offset = u64::from_le_bytes(extents[16..24].try_into().unwrap_or_default());
    extent_disk == disk && extent_offset == offset
}

/// A volume held locked and dismounted. Nothing else can read or write it until this is
/// dropped (or the partition table change removes the volume).
#[derive(Debug)]
pub struct LockedVolume {
    path: String,
    _handle: Handle,
}

impl LockedVolume {
    /// Lock and dismount. Never forced: if anything has the volume open, this refuses.
    pub fn lock(path: &str) -> Result<Self, PlatformError> {
        let handle = Handle::open(path.trim_end_matches('\\'), GENERIC_READ | GENERIC_WRITE, 0)?;
        let mut locked = false;
        for _ in 0..10 {
            if handle.ioctl(FSCTL_LOCK_VOLUME, &[], &mut []).is_ok() {
                locked = true;
                break;
            }
            sleep(Duration::from_millis(200));
        }
        if !locked {
            return Err(PlatformError::Refused(format!(
                "could not lock {path}: files on it are open. Close the programs or stop the services using it and try again"
            )));
        }
        handle.ioctl(FSCTL_DISMOUNT_VOLUME, &[], &mut [])?;
        Ok(Self {
            path: path.to_owned(),
            _handle: handle,
        })
    }

    pub fn path(&self) -> &str {
        &self.path
    }
}
