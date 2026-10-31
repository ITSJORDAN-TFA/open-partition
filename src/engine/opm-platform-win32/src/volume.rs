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
const FSCTL_GET_NTFS_VOLUME_DATA: u32 = 0x0009_0064;
const FSCTL_GET_VOLUME_BITMAP: u32 = 0x0009_006F;
/// Free runs shorter than this are copied anyway: not worth a separate I/O.
const MIN_FREE_RUN: u64 = 1 << 20;

/// Volume GUID paths (`\\?\Volume{...}\`) of every volume that lies inside
/// `[start, end)` on `disk`: one partition, or each logical drive of an extended partition.
pub fn find_volumes_in(disk: u32, start: u64, end: u64) -> Result<Vec<String>, IoError> {
    let mut name = [0u16; 512];
    #[allow(unsafe_code)]
    // SAFETY: `name` is writable for the length given.
    let find = unsafe { FindFirstVolumeW(name.as_mut_ptr(), name.len() as u32) };
    if find == INVALID_HANDLE_VALUE {
        return Err(crate::handle::last_error());
    }
    let mut found = Vec::new();
    loop {
        let len = name.iter().position(|&c| c == 0).unwrap_or(name.len());
        let path = String::from_utf16_lossy(&name[..len]);
        if extent_of(&path).is_some_and(|(d, offset, length)| {
            d == disk && start <= offset && offset + length <= end
        }) {
            found.push(path);
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

/// (disk, offset, length) of a single-extent volume.
fn extent_of(path: &str) -> Option<(u32, u64, u64)> {
    extent_via(&Handle::open(path.trim_end_matches('\\'), 0, 0).ok()?)
}

/// [`extent_of`] through a handle already open on the volume. A locked volume refuses new
/// handles, so once it is locked this is the only way to ask.
fn extent_via(handle: &Handle) -> Option<(u32, u64, u64)> {
    let extents = handle
        .ioctl_vec(IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, &[], 256)
        .ok()?;
    // VOLUME_DISK_EXTENTS: count, pad, then { DiskNumber, pad, StartingOffset, ExtentLength }.
    if extents.len() < 32 || u32::from_le_bytes(extents[0..4].try_into().ok()?) != 1 {
        return None;
    }
    Some((
        u32::from_le_bytes(extents[8..12].try_into().ok()?),
        u64::from_le_bytes(extents[16..24].try_into().ok()?),
        u64::from_le_bytes(extents[24..32].try_into().ok()?),
    ))
}

/// Lock and dismount every volume in `[start, end)` on `disk`. All or nothing.
pub fn lock_volumes_in(
    disk: u32,
    start: u64,
    end: u64,
) -> Result<Vec<LockedVolume>, PlatformError> {
    find_volumes_in(disk, start, end)?
        .iter()
        .map(|path| LockedVolume::lock(path))
        .collect()
}

/// A volume held locked and dismounted. Nothing else can read or write it until this is
/// dropped (or the partition table change removes the volume).
#[derive(Debug)]
pub struct LockedVolume {
    path: String,
    free: Vec<(u64, u64)>,
    free_unknown: Option<String>,
    _handle: Handle,
}

impl LockedVolume {
    /// Lock and dismount. Never forced: if anything has the volume open, this refuses.
    pub fn lock(path: &str) -> Result<Self, PlatformError> {
        let handle = Handle::open(path.trim_end_matches('\\'), GENERIC_READ | GENERIC_WRITE, 0)?;
        let mut locked = false;
        // Up to ~10 s: Explorer, the indexer or antivirus often hold a volume briefly.
        for _ in 0..50 {
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
        // Read while locked (nothing can allocate) and before the dismount (which ends file
        // system requests on this handle). Not NTFS, or unreadable: everything gets copied.
        let (free, free_unknown) = match extent_via(&handle) {
            None => (Vec::new(), Some("volume extent unreadable".to_owned())),
            Some((_, offset, _)) => match ntfs_free_runs(&handle, offset) {
                Ok(runs) => (runs, None),
                Err(e) => (
                    Vec::new(),
                    Some(format!("not NTFS or bitmap unreadable ({e})")),
                ),
            },
        };
        handle.ioctl(FSCTL_DISMOUNT_VOLUME, &[], &mut [])?;
        Ok(Self {
            path: path.to_owned(),
            free,
            free_unknown,
            _handle: handle,
        })
    }

    pub fn path(&self) -> &str {
        &self.path
    }

    /// Runs of free NTFS clusters, as absolute disk byte ranges, read under the lock. Empty if
    /// the volume isn't NTFS.
    pub fn free_runs(&self) -> &[(u64, u64)] {
        &self.free
    }

    /// Why the free space could not be read (then the whole volume is copied).
    pub fn free_unknown(&self) -> Option<&str> {
        self.free_unknown.as_deref()
    }
}

/// Free cluster runs of at least [`MIN_FREE_RUN`] bytes on the NTFS volume open on `handle`,
/// whose cluster 0 starts at disk byte `offset`.
fn ntfs_free_runs(handle: &Handle, offset: u64) -> Result<Vec<(u64, u64)>, IoError> {
    // NTFS_VOLUME_DATA_BUFFER: TotalClusters at 16, BytesPerCluster at 44.
    let mut data = [0u8; 96];
    handle.ioctl(FSCTL_GET_NTFS_VOLUME_DATA, &[], &mut data)?;
    let total = u64::from_le_bytes(data[16..24].try_into().unwrap_or_default());
    let cluster = u64::from(u32::from_le_bytes(
        data[44..48].try_into().unwrap_or_default(),
    ));
    if total == 0 || cluster == 0 {
        return Ok(Vec::new());
    }
    // VOLUME_BITMAP_BUFFER: StartingLcn, BitmapSize (clusters), then one bit per cluster.
    let mut bitmap = vec![0u8; 16 + total.div_ceil(8) as usize];
    handle.ioctl(FSCTL_GET_VOLUME_BITMAP, &0u64.to_le_bytes(), &mut bitmap)?;
    let clusters = u64::from_le_bytes(bitmap[8..16].try_into().unwrap_or_default()).min(total);
    let bits = &bitmap[16..];
    let min = MIN_FREE_RUN.div_ceil(cluster);
    let mut runs = Vec::new();
    let mut lcn = 0u64;
    while lcn < clusters {
        let byte = bits[(lcn / 8) as usize];
        if lcn % 8 == 0 && byte == 0xFF {
            lcn += 8;
            continue;
        }
        if byte >> (lcn % 8) & 1 == 1 {
            lcn += 1;
            continue;
        }
        let start = lcn;
        while lcn < clusters {
            let byte = bits[(lcn / 8) as usize];
            if lcn % 8 == 0 && byte == 0 && lcn + 8 <= clusters {
                lcn += 8;
            } else if byte >> (lcn % 8) & 1 == 0 {
                lcn += 1;
            } else {
                break;
            }
        }
        if lcn - start >= min {
            runs.push((offset + start * cluster, (lcn - start) * cluster));
        }
    }
    Ok(runs)
}
