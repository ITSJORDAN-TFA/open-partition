use std::alloc::{alloc_zeroed, dealloc, Layout as AllocLayout};

use opm_core::{
    BlockDevice, DiskIdentity, IoError, PartitionEntry, PartitionKey, PartitionTable, RangeDigest,
};
use sha2::{Digest, Sha256};
use windows_sys::Win32::Storage::FileSystem::{
    ReadFile, WriteFile, FILE_FLAG_NO_BUFFERING, FILE_FLAG_WRITE_THROUGH,
};
use windows_sys::Win32::System::IO::OVERLAPPED;

use crate::guard::require_virtual_disk;
use crate::handle::{last_error, Handle, GENERIC_READ, GENERIC_WRITE};
use crate::layout::{Layout, LayoutStyle};
use crate::PlatformError;

const IOCTL_DISK_GET_DRIVE_GEOMETRY_EX: u32 = 0x0007_00A0;
const IOCTL_DISK_GET_DRIVE_LAYOUT_EX: u32 = 0x0007_0050;
const IOCTL_DISK_SET_DRIVE_LAYOUT_EX: u32 = 0x0007_C054;
const IOCTL_DISK_UPDATE_PROPERTIES: u32 = 0x0007_0140;

/// Unbuffered I/O needs sector-aligned memory; this is the bounce buffer for it.
const BOUNCE_SIZE: usize = 4 << 20;
const BOUNCE_ALIGN: usize = 4096;

#[derive(Debug)]
struct AlignedBuffer {
    ptr: *mut u8,
    len: usize,
}

impl AlignedBuffer {
    fn new(len: usize) -> Self {
        let layout = AllocLayout::from_size_align(len, BOUNCE_ALIGN).expect("valid bounce layout");
        #[allow(unsafe_code)]
        // SAFETY: the layout has non-zero size.
        let ptr = unsafe { alloc_zeroed(layout) };
        assert!(!ptr.is_null(), "out of memory allocating the I/O buffer");
        Self { ptr, len }
    }

    fn as_mut_slice(&mut self) -> &mut [u8] {
        #[allow(unsafe_code)]
        // SAFETY: `ptr` points to `len` initialised bytes owned by `self`.
        unsafe {
            std::slice::from_raw_parts_mut(self.ptr, self.len)
        }
    }
}

impl Drop for AlignedBuffer {
    fn drop(&mut self) {
        #[allow(unsafe_code)]
        // SAFETY: allocated in `new` with exactly this layout.
        unsafe {
            dealloc(
                self.ptr,
                AllocLayout::from_size_align_unchecked(self.len, BOUNCE_ALIGN),
            );
        }
    }
}

fn overlapped_at(offset: u64) -> OVERLAPPED {
    #[allow(unsafe_code)]
    // SAFETY: OVERLAPPED is plain data; all-zero is its documented initial state.
    let mut o: OVERLAPPED = unsafe { std::mem::zeroed() };
    o.Anonymous.Anonymous.Offset = offset as u32;
    o.Anonymous.Anonymous.OffsetHigh = (offset >> 32) as u32;
    o
}

/// `\\.\PhysicalDriveN` opened for unbuffered, write-through I/O. Only file-backed virtual
/// disks can be opened (see the crate docs).
#[derive(Debug)]
pub struct PhysicalDisk {
    number: u32,
    handle: Handle,
    sector_size: u32,
    size: u64,
    bounce: AlignedBuffer,
}

impl PhysicalDisk {
    pub fn open(number: u32) -> Result<Self, PlatformError> {
        require_virtual_disk(number)?;
        let handle = Handle::open(
            &format!(r"\\.\PhysicalDrive{number}"),
            GENERIC_READ | GENERIC_WRITE,
            FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
        )?;
        let geometry = handle.ioctl_vec(IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, &[], 256)?;
        if geometry.len() < 32 {
            return Err(IoError::Os(13).into());
        }
        let sector_size = u32::from_le_bytes(geometry[20..24].try_into().unwrap_or_default());
        let size = u64::from_le_bytes(geometry[24..32].try_into().unwrap_or_default());
        if !(sector_size == 512 || sector_size == 4096) {
            return Err(PlatformError::Refused(format!(
                "unsupported sector size {sector_size}"
            )));
        }
        Ok(Self {
            number,
            handle,
            sector_size,
            size,
            bounce: AlignedBuffer::new(BOUNCE_SIZE),
        })
    }

    pub fn number(&self) -> u32 {
        self.number
    }

    pub fn layout(&self) -> Result<Layout, IoError> {
        Layout::parse(
            self.handle
                .ioctl_vec(IOCTL_DISK_GET_DRIVE_LAYOUT_EX, &[], 64 * 1024)?,
        )
    }

    pub fn identity(&self) -> Result<DiskIdentity, IoError> {
        Ok(DiskIdentity {
            id: self.layout()?.disk_id(),
        })
    }

    /// Write a modified layout, ask Windows to re-read it, and flush.
    fn write_layout(&mut self, layout: &Layout) -> Result<(), IoError> {
        let buffer = layout.to_set_buffer();
        self.handle
            .ioctl(IOCTL_DISK_SET_DRIVE_LAYOUT_EX, &buffer, &mut [])?;
        self.handle
            .ioctl(IOCTL_DISK_UPDATE_PROPERTIES, &[], &mut [])?;
        self.handle.flush()
    }

    /// Refuse a move unless `[target, target + length)` is 1 MiB aligned, inside the usable
    /// area, and overlaps no partition other than the one being moved.
    pub fn check_move_target(
        &self,
        key: PartitionKey,
        target: u64,
        length: u64,
    ) -> Result<(), PlatformError> {
        const MIB: u64 = 1 << 20;
        let layout = self.layout()?;
        let Some(entry) = layout.find(key) else {
            return Err(PlatformError::Refused("partition not found".into()));
        };
        if layout.style == LayoutStyle::Raw {
            return Err(PlatformError::Refused("disk has no partition table".into()));
        }
        if target % MIB != 0 {
            return Err(PlatformError::Refused(format!(
                "target offset {target} is not on a 1 MiB boundary"
            )));
        }
        let end = target
            .checked_add(length)
            .ok_or_else(|| PlatformError::Refused("target overflows".into()))?;
        let (usable_start, usable_end) = layout.usable_range(self.size, self.sector_size);
        if target < usable_start || end > usable_end {
            return Err(PlatformError::Refused(format!(
                "target range {target}..{end} is outside the usable area {usable_start}..{usable_end}"
            )));
        }
        if let Some(other) = layout
            .entries()
            .into_iter()
            .filter(|e| e.index != entry.index)
            .find(|e| target < e.offset + e.length && e.offset < end)
        {
            return Err(PlatformError::Refused(format!(
                "target range overlaps partition {} at {}..{}",
                other.number,
                other.offset,
                other.offset + other.length
            )));
        }
        Ok(())
    }
}

impl BlockDevice for PhysicalDisk {
    fn sector_size(&self) -> u32 {
        self.sector_size
    }

    fn size_bytes(&self) -> u64 {
        self.size
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<(), IoError> {
        self.check_request(offset, buf.len())?;
        let mut done = 0usize;
        while done < buf.len() {
            let n = (buf.len() - done).min(BOUNCE_SIZE);
            let mut overlapped = overlapped_at(offset + done as u64);
            let mut read = 0u32;
            let bounce = self.bounce.as_mut_slice();
            #[allow(unsafe_code)]
            // SAFETY: the bounce buffer is sector aligned and at least `n` bytes; the handle is
            // synchronous, so OVERLAPPED only carries the offset and outlives the call.
            let ok = unsafe {
                ReadFile(
                    self.handle.raw(),
                    bounce.as_mut_ptr(),
                    n as u32,
                    &mut read,
                    &mut overlapped,
                )
            };
            if ok == 0 {
                return Err(last_error());
            }
            if read as usize != n {
                return Err(IoError::OutOfRange);
            }
            buf[done..done + n].copy_from_slice(&bounce[..n]);
            done += n;
        }
        Ok(())
    }

    fn write_at(&mut self, offset: u64, data: &[u8]) -> Result<(), IoError> {
        self.check_request(offset, data.len())?;
        let mut done = 0usize;
        while done < data.len() {
            let n = (data.len() - done).min(BOUNCE_SIZE);
            let bounce = self.bounce.as_mut_slice();
            bounce[..n].copy_from_slice(&data[done..done + n]);
            let mut overlapped = overlapped_at(offset + done as u64);
            let mut written = 0u32;
            #[allow(unsafe_code)]
            // SAFETY: as in `read_at`.
            let ok = unsafe {
                WriteFile(
                    self.handle.raw(),
                    bounce.as_ptr(),
                    n as u32,
                    &mut written,
                    &mut overlapped,
                )
            };
            if ok == 0 {
                return Err(last_error());
            }
            if written as usize != n {
                return Err(IoError::OutOfRange);
            }
            done += n;
        }
        Ok(())
    }

    fn flush(&mut self) -> Result<(), IoError> {
        self.handle.flush()
    }
}

impl PartitionTable for PhysicalDisk {
    fn partition_start(&mut self, partition: PartitionKey) -> Result<Option<u64>, IoError> {
        Ok(self.layout()?.find(partition).map(|e| e.offset))
    }

    fn entry(&mut self, partition: PartitionKey) -> Result<PartitionEntry, IoError> {
        let layout = self.layout()?;
        let entry = layout.find(partition).ok_or(IoError::Os(1168))?; // ERROR_NOT_FOUND
        Ok(layout.snapshot(&entry))
    }

    fn detach(&mut self, partition: PartitionKey) -> Result<(), IoError> {
        let mut layout = self.layout()?;
        let Some(entry) = layout.find(partition) else {
            return Ok(());
        };
        layout.remove(entry.index);
        self.write_layout(&layout)
    }

    fn attach(
        &mut self,
        partition: PartitionKey,
        entry: &PartitionEntry,
        start: u64,
    ) -> Result<(), IoError> {
        let mut layout = self.layout()?;
        layout.insert(partition, entry, start, self.sector_size)?;
        self.write_layout(&layout)
    }

    fn range_is_free(&mut self, start: u64, length: u64) -> Result<bool, IoError> {
        Ok(self.layout()?.range_is_free(start, length))
    }
}

impl RangeDigest for PhysicalDisk {
    /// SHA-256 of a byte range, read unbuffered from the disk.
    fn range_digest(&mut self, offset: u64, length: u64) -> Result<[u8; 32], IoError> {
        let mut hasher = Sha256::new();
        let mut buffer = vec![0u8; BOUNCE_SIZE];
        let mut done = 0u64;
        while done < length {
            let n = (length - done).min(BOUNCE_SIZE as u64) as usize;
            self.read_at(offset + done, &mut buffer[..n])?;
            hasher.update(&buffer[..n]);
            done += n as u64;
        }
        Ok(hasher.finalize().into())
    }
}
