use std::thread::sleep;
use std::time::Duration;

use opm_core::IoError;
use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
use windows_sys::Win32::Storage::FileSystem::{
    FindFirstVolumeW, FindNextVolumeW, FindVolumeClose, ReadFile,
};

use crate::disk::{overlapped_at, AlignedBuffer, BOUNCE_ALIGN};
use crate::handle::{Handle, GENERIC_READ, GENERIC_WRITE};
use crate::ntfs::{self, Bitmap, NtfsError};
use crate::PlatformError;

const IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS: u32 = 0x0056_0000;
const FSCTL_LOCK_VOLUME: u32 = 0x0009_0018;
const FSCTL_DISMOUNT_VOLUME: u32 = 0x0009_0020;
const FSCTL_GET_NTFS_VOLUME_DATA: u32 = 0x0009_0064;
const FSCTL_GET_VOLUME_BITMAP: u32 = 0x0009_006F;
/// Free runs shorter than this are copied anyway: not worth a separate I/O.
const MIN_FREE_RUN: u64 = 1 << 20;
/// Raw metadata reads go through a bounce buffer of this size.
const RAW_CHUNK: usize = 1 << 20;

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
    free_source: String,
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
        // Free space is read under the lock, so nothing can allocate in between. First the file
        // system's own bitmap (works on Windows 11; Server 2016 answers ERROR_NOT_READY), which
        // has to be asked before the dismount ends file system requests on this handle.
        let extent = extent_via(&handle);
        let fsctl = extent.map(|_| fsctl_bitmap(&handle));
        handle.ioctl(FSCTL_DISMOUNT_VOLUME, &[], &mut [])?;
        // Then the on-disk $Bitmap, read raw through the locking handle: after the dismount it
        // is final. Not NTFS, or unreadable both ways: everything gets copied.
        let (free, free_unknown, free_source) = match extent {
            None => (
                Vec::new(),
                Some("volume extent unreadable".to_owned()),
                String::new(),
            ),
            Some((_, offset, length)) => {
                let mut bounce = AlignedBuffer::new(RAW_CHUNK);
                let raw = ntfs::read_bitmap(length, &mut |at, buf| {
                    raw_read(&handle, &mut bounce, length, at, buf)
                });
                match choose_bitmap(raw, fsctl.unwrap_or(Err(IoError::OutOfRange))) {
                    Ok((bitmap, source)) => {
                        (ntfs::free_runs(&bitmap, offset, MIN_FREE_RUN), None, source)
                    }
                    Err(why) => (Vec::new(), Some(why), String::new()),
                }
            }
        };
        Ok(Self {
            path: path.to_owned(),
            free,
            free_unknown,
            free_source,
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

    /// Where the free space came from: `raw+fsctl` (both reads, intersected), `raw` (the
    /// on-disk `$Bitmap` only) or `fsctl` (the file system only), with why the other failed.
    pub fn free_source(&self) -> &str {
        &self.free_source
    }
}

/// Which bitmap to trust. When both reads work a cluster is free only if both say so; when one
/// fails the other is used; a dirty volume or two failures mean the whole volume is copied.
/// Returns the bitmap and where it came from.
fn choose_bitmap(
    raw: Result<Bitmap, NtfsError>,
    fsctl: Result<Bitmap, IoError>,
) -> Result<(Bitmap, String), String> {
    match (raw, fsctl) {
        (Err(NtfsError::Dirty), _) => Err(NtfsError::Dirty.to_string()),
        (Ok(raw), Ok(fsctl)) => ntfs::intersect_free(&raw, &fsctl)
            .map(|b| (b, "raw+fsctl".to_owned()))
            .ok_or_else(|| {
                "the file system and the on-disk bitmap disagree about the volume size".to_owned()
            }),
        (Ok(bitmap), Err(fsctl)) => Ok((bitmap, format!("raw (FSCTL: {fsctl})"))),
        (Err(raw), Ok(bitmap)) => Ok((bitmap, format!("fsctl ({raw})"))),
        (Err(raw), Err(fsctl)) => Err(format!(
            "not NTFS or bitmap unreadable (FSCTL: {fsctl}; {raw})"
        )),
    }
}

/// The allocation bitmap the file system reports (`FSCTL_GET_VOLUME_BITMAP`) for the NTFS
/// volume open on `handle`.
fn fsctl_bitmap(handle: &Handle) -> Result<Bitmap, IoError> {
    // NTFS_VOLUME_DATA_BUFFER: TotalClusters at 16, BytesPerCluster at 44.
    let mut data = [0u8; 96];
    handle.ioctl(FSCTL_GET_NTFS_VOLUME_DATA, &[], &mut data)?;
    let total = u64::from_le_bytes(data[16..24].try_into().unwrap_or_default());
    let cluster = u64::from(u32::from_le_bytes(
        data[44..48].try_into().unwrap_or_default(),
    ));
    if total == 0 || cluster == 0 {
        return Err(IoError::OutOfRange);
    }
    // VOLUME_BITMAP_BUFFER: StartingLcn, BitmapSize (clusters), then one bit per cluster.
    let mut buffer = vec![0u8; 16 + total.div_ceil(8) as usize];
    handle.ioctl(FSCTL_GET_VOLUME_BITMAP, &0u64.to_le_bytes(), &mut buffer)?;
    if u64::from_le_bytes(buffer[8..16].try_into().unwrap_or_default()) != total {
        return Err(IoError::OutOfRange);
    }
    Ok(Bitmap {
        cluster,
        clusters: total,
        bits: buffer.split_off(16),
    })
}

/// Read `buf.len()` bytes at byte `offset` of a volume of `volume_len` bytes through `handle`,
/// in sector-aligned pieces (raw volume reads must be aligned).
fn raw_read(
    handle: &Handle,
    bounce: &mut AlignedBuffer,
    volume_len: u64,
    offset: u64,
    buf: &mut [u8],
) -> Result<(), IoError> {
    let align = BOUNCE_ALIGN as u64;
    let mut done = 0usize;
    while done < buf.len() {
        let at = offset + done as u64;
        let start = at / align * align;
        // A volume whose length isn't a multiple of 4 KiB has 512-byte sectors, so clamping to
        // its end keeps the read aligned.
        let end = at
            .checked_add((buf.len() - done) as u64)
            .ok_or(IoError::OutOfRange)?
            .next_multiple_of(align)
            .min(start + RAW_CHUNK as u64)
            .min(volume_len);
        if end <= at {
            return Err(IoError::OutOfRange);
        }
        let n = (end - start) as usize;
        let piece = bounce.as_mut_slice();
        let mut overlapped = overlapped_at(start);
        let mut read = 0u32;
        #[allow(unsafe_code)]
        // SAFETY: the bounce buffer is 4 KiB aligned and at least `n` bytes; the handle is
        // synchronous, so OVERLAPPED only carries the offset and outlives the call.
        let ok = unsafe {
            ReadFile(
                handle.raw(),
                piece.as_mut_ptr(),
                n as u32,
                &mut read,
                &mut overlapped,
            )
        };
        if ok == 0 {
            return Err(crate::handle::last_error());
        }
        if read as usize != n {
            return Err(IoError::OutOfRange);
        }
        let skip = (at - start) as usize;
        let take = (n - skip).min(buf.len() - done);
        buf[done..done + take].copy_from_slice(&piece[skip..skip + take]);
        done += take;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bitmap(bits: &[u8]) -> Bitmap {
        Bitmap {
            cluster: 4096,
            clusters: bits.len() as u64 * 8,
            bits: bits.to_vec(),
        }
    }

    #[test]
    fn both_bitmaps_must_agree_a_cluster_is_free() {
        let chosen = choose_bitmap(Ok(bitmap(&[0x01, 0x00])), Ok(bitmap(&[0x00, 0x80])));
        let (bitmap, source) = chosen.unwrap();
        assert_eq!(
            (bitmap.bits, source.as_str()),
            (vec![0x01, 0x80], "raw+fsctl")
        );
    }

    #[test]
    fn one_working_read_is_enough_and_none_means_a_full_copy() {
        let not_ready = || Err(IoError::Os(21));
        let (raw, source) = choose_bitmap(Ok(bitmap(&[0x0F])), not_ready()).unwrap();
        assert_eq!(raw.bits, vec![0x0F]);
        assert!(
            source.starts_with("raw (FSCTL: OS error 0x00000015"),
            "{source}"
        );
        let (fsctl, source) =
            choose_bitmap(Err(NtfsError::Invalid("x")), Ok(bitmap(&[0x0F]))).unwrap();
        assert_eq!(fsctl.bits, vec![0x0F]);
        assert!(
            source.starts_with("fsctl (unexpected NTFS layout"),
            "{source}"
        );
        let why =
            choose_bitmap(Err(NtfsError::Invalid("MFT record number")), not_ready()).unwrap_err();
        assert!(
            why.contains("0x00000015") && why.contains("MFT record number"),
            "{why}"
        );
    }

    #[test]
    fn a_dirty_volume_is_copied_whole_even_if_the_fsctl_works() {
        let why = choose_bitmap(Err(NtfsError::Dirty), Ok(bitmap(&[0x0F]))).unwrap_err();
        assert!(why.contains("chkdsk"), "{why}");
    }

    #[test]
    fn bitmaps_of_different_volumes_are_not_trusted() {
        let mut other = bitmap(&[0x00, 0x00]);
        other.cluster = 512;
        assert!(choose_bitmap(Ok(bitmap(&[0x00, 0x00])), Ok(other)).is_err());
    }
}
