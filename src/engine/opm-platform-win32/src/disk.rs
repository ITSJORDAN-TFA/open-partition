use std::alloc::{alloc_zeroed, dealloc, Layout as AllocLayout};
use std::thread;
use std::time::{Duration, Instant};

use opm_core::{
    BlockDevice, DiskIdentity, IoError, PartitionEntry, PartitionKey, PartitionTable, RangeDigest,
};
use sha2::{Digest, Sha256};
use windows_sys::Win32::Storage::FileSystem::{
    ReadFile, WriteFile, FILE_FLAG_NO_BUFFERING, FILE_FLAG_WRITE_THROUGH,
};
use windows_sys::Win32::System::IO::OVERLAPPED;

use crate::guard::{require_writable, WriteAccess};
use crate::handle::{last_error, Handle, GENERIC_READ, GENERIC_WRITE};
use crate::layout::{is_extended_type, snapshot_records, Layout, LayoutStyle};
use crate::mounted_devices::{self, Remap};
use crate::PlatformError;

const IOCTL_DISK_GET_DRIVE_GEOMETRY_EX: u32 = 0x0007_00A0;
const IOCTL_DISK_GET_DRIVE_LAYOUT_EX: u32 = 0x0007_0050;
const IOCTL_DISK_SET_DRIVE_LAYOUT_EX: u32 = 0x0007_C054;
const IOCTL_DISK_UPDATE_PROPERTIES: u32 = 0x0007_0140;

/// Large reads and writes are split into pieces of this size and sent through [`LANES`]
/// handles at once, so the disk always has several requests in flight (cloud disks only reach
/// their rated throughput that way). Each lane has its own sector-aligned bounce buffer, which
/// unbuffered I/O needs.
const PIECE: usize = 8 << 20;
const LANES: usize = 4;
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

/// One parallel I/O path: its own handle to the disk and its own bounce buffer.
#[derive(Debug)]
struct Lane {
    handle: Handle,
    bounce: AlignedBuffer,
}

// SAFETY: a lane exclusively owns its handle (a kernel handle, usable from any thread) and its
// heap buffer; it is only ever used by one thread at a time (moved in by `&mut`).
#[allow(unsafe_code)]
unsafe impl Send for Lane {}

impl Lane {
    fn open(path: &str, access: u32) -> Result<Self, IoError> {
        Ok(Self {
            handle: Handle::open(
                path,
                access,
                FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
            )?,
            bounce: AlignedBuffer::new(PIECE),
        })
    }

    /// Read one piece (at most [`PIECE`] bytes).
    fn read(&mut self, offset: u64, buf: &mut [u8]) -> Result<(), IoError> {
        let n = buf.len();
        let mut overlapped = overlapped_at(offset);
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
        buf.copy_from_slice(&bounce[..n]);
        Ok(())
    }

    /// Write one piece (at most [`PIECE`] bytes).
    fn write(&mut self, offset: u64, data: &[u8]) -> Result<(), IoError> {
        let n = data.len();
        let bounce = self.bounce.as_mut_slice();
        bounce[..n].copy_from_slice(data);
        let mut overlapped = overlapped_at(offset);
        let mut written = 0u32;
        #[allow(unsafe_code)]
        // SAFETY: as in `read`.
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
        Ok(())
    }
}

/// Run `work` on every item, spreading the items round-robin over the lanes, one thread per
/// lane. A single item runs on the calling thread.
fn on_lanes<T: Send>(
    lanes: &mut [Lane],
    items: Vec<T>,
    work: impl Fn(&mut Lane, T) -> Result<(), IoError> + Sync,
) -> Result<(), IoError> {
    if items.len() <= 1 {
        return items
            .into_iter()
            .try_for_each(|item| work(&mut lanes[0], item));
    }
    let mut groups: Vec<Vec<T>> = lanes.iter().map(|_| Vec::new()).collect();
    let count = groups.len();
    for (i, item) in items.into_iter().enumerate() {
        groups[i % count].push(item);
    }
    let work = &work;
    thread::scope(|scope| {
        let running: Vec<_> = lanes
            .iter_mut()
            .zip(groups)
            .filter(|(_, group)| !group.is_empty())
            .map(|(lane, group)| {
                scope.spawn(move || group.into_iter().try_for_each(|item| work(lane, item)))
            })
            .collect();
        // Join every lane before reporting, so no I/O is still running when this returns.
        let results: Vec<_> = running
            .into_iter()
            .map(|t| t.join().unwrap_or(Err(IoError::Os(-1))))
            .collect();
        results.into_iter().collect()
    })
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
/// disks can be opened for writing (see the crate docs); any disk can be opened read-only.
#[derive(Debug)]
pub struct PhysicalDisk {
    number: u32,
    handle: Handle,
    sector_size: u32,
    size: u64,
    lanes: Vec<Lane>,
    writable: bool,
}

/// What [`PhysicalDisk::bench`] measured: `bytes` read in `elapsed`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BenchResult {
    pub bytes: u64,
    pub elapsed: Duration,
}

impl BenchResult {
    pub fn bytes_per_second(&self) -> u64 {
        let micros = self.elapsed.as_micros().max(1);
        u64::try_from(u128::from(self.bytes) * 1_000_000 / micros).unwrap_or(u64::MAX)
    }
}

/// Access rights for a disk opened for writing, or for reading only.
fn access_for(writable: bool) -> u32 {
    if writable {
        GENERIC_READ | GENERIC_WRITE
    } else {
        GENERIC_READ
    }
}

/// What a write to a read-only disk fails with (Windows' ERROR_ACCESS_DENIED).
const ERROR_ACCESS_DENIED: i32 = 5;

impl PhysicalDisk {
    /// Open a test virtual disk.
    pub fn open(number: u32) -> Result<Self, PlatformError> {
        Self::open_with(number, &WriteAccess::VirtualOnly)
    }

    /// Open any disk `access` allows (see the write guard).
    pub fn open_with(number: u32, access: &WriteAccess) -> Result<Self, PlatformError> {
        require_writable(number, access)?;
        Self::open_path(number, true)
    }

    /// Open any disk for reading only, through the same unbuffered, parallel path a move uses.
    /// No handle has write access, so Windows itself refuses any write; the write guard isn't
    /// needed for that and isn't consulted.
    pub fn open_read_only(number: u32) -> Result<Self, PlatformError> {
        Self::open_path(number, false)
    }

    fn open_path(number: u32, writable: bool) -> Result<Self, PlatformError> {
        let path = format!(r"\\.\PhysicalDrive{number}");
        let access = access_for(writable);
        let handle = Handle::open(
            &path,
            access,
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
            lanes: (0..LANES)
                .map(|_| Lane::open(&path, access))
                .collect::<Result<_, _>>()?,
            writable,
        })
    }

    /// An ordinary file opened like a disk, to test the parallel I/O without a real disk.
    #[cfg(test)]
    fn open_file_for_test(path: &str, size: u64) -> Result<Self, IoError> {
        Self::open_file_for_test_with(path, size, true)
    }

    #[cfg(test)]
    fn open_file_for_test_with(path: &str, size: u64, writable: bool) -> Result<Self, IoError> {
        let access = access_for(writable);
        Ok(Self {
            number: u32::MAX,
            handle: Handle::open(
                path,
                access,
                FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
            )?,
            sector_size: 4096,
            size,
            lanes: (0..LANES)
                .map(|_| Lane::open(path, access))
                .collect::<Result<_, _>>()?,
            writable,
        })
    }

    pub fn is_writable(&self) -> bool {
        self.writable
    }

    fn require_write_access(&self) -> Result<(), IoError> {
        if self.writable {
            Ok(())
        } else {
            Err(IoError::Os(ERROR_ACCESS_DENIED))
        }
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
        self.require_write_access()?;
        let buffer = layout.to_set_buffer();
        self.handle
            .ioctl(IOCTL_DISK_SET_DRIVE_LAYOUT_EX, &buffer, &mut [])?;
        self.handle
            .ioctl(IOCTL_DISK_UPDATE_PROPERTIES, &[], &mut [])?;
        self.handle.flush()
    }

    /// Grow the MBR extended partition starting at `offset` to `new_length` bytes, into free
    /// space after it. One table write; no data moves. The new end must be on a 1 MiB boundary
    /// (or the end of the disk).
    pub fn grow_extended(&mut self, offset: u64, new_length: u64) -> Result<(), PlatformError> {
        const MIB: u64 = 1 << 20;
        let mut layout = self.layout()?;
        let Some(entry) = layout
            .entries()
            .into_iter()
            .find(|e| e.offset == offset && layout.is_container(e))
        else {
            return Err(PlatformError::Refused(format!(
                "no extended partition starts at byte {offset}"
            )));
        };
        let end = offset
            .checked_add(new_length)
            .ok_or_else(|| PlatformError::Refused("length overflows".into()))?;
        if end > self.size || (end % MIB != 0 && end != self.size) {
            return Err(PlatformError::Refused(format!(
                "new end {end} must be on a 1 MiB boundary inside the disk"
            )));
        }
        layout.grow_container(&entry, new_length).map_err(|e| match e {
            IoError::Os(183) => PlatformError::Refused(
                "another partition is in the way; the extended partition can only grow into free space directly after it".into(),
            ),
            _ => PlatformError::Refused("only an extended partition can be grown this way, and only to a larger size".into()),
        })?;
        self.write_layout(&layout)?;
        Ok(())
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
        // An extended partition moves with the logical drives inside it.
        let moving_with = |e: &crate::layout::LayoutEntry| {
            e.index == entry.index || (layout.is_container(&entry) && e.index >= 4)
        };
        if let Some(other) = layout
            .entries()
            .into_iter()
            .filter(|e| !moving_with(e))
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
        let pieces: Vec<(u64, &mut [u8])> = buf
            .chunks_mut(PIECE)
            .enumerate()
            .map(|(i, piece)| (offset + (i * PIECE) as u64, piece))
            .collect();
        on_lanes(&mut self.lanes, pieces, |lane, (at, piece)| {
            lane.read(at, piece)
        })
    }

    fn write_at(&mut self, offset: u64, data: &[u8]) -> Result<(), IoError> {
        self.require_write_access()?;
        self.check_request(offset, data.len())?;
        let pieces: Vec<(u64, &[u8])> = data
            .chunks(PIECE)
            .enumerate()
            .map(|(i, piece)| (offset + (i * PIECE) as u64, piece))
            .collect();
        on_lanes(&mut self.lanes, pieces, |lane, (at, piece)| {
            lane.write(at, piece)
        })
    }

    /// Writes made through every lane reach the disk: the flush goes to the device.
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
        layout.snapshot(&entry)
    }

    fn detach(&mut self, partition: PartitionKey) -> Result<(), IoError> {
        let mut layout = self.layout()?;
        let Some(entry) = layout.find(partition) else {
            return Ok(());
        };
        layout.remove(&entry);
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
        if layout.style != LayoutStyle::Mbr {
            return self.write_layout(&layout);
        }
        // MBR volumes are known by disk signature + start: move their drive letters and GUIDs
        // to the new starts before the partitions reappear (plan gap G1).
        let records = snapshot_records(entry)?;
        let first = records[0].offset;
        let moves = records
            .iter()
            .filter(|r| r.mbr_type.is_some_and(|t| !is_extended_type(t)))
            .map(|r| (r.offset, r.offset - first + start))
            .filter(|(old, new)| old != new)
            .collect::<Vec<_>>();
        if moves.is_empty() {
            // Back where it started (a rollback): the IDs are unchanged.
            return self.write_layout(&layout);
        }
        mounted_devices::remap(&Remap {
            signature: layout.signature(),
            moves,
        })?;
        self.write_layout(&layout)?;
        // The partitions are back, so recovery will never replay the remap. A stale plan left
        // by a failure here only matches an identical future remap, so it is not an error.
        let _ = mounted_devices::finish();
        Ok(())
    }

    fn range_is_free(&mut self, start: u64, length: u64) -> Result<bool, IoError> {
        Ok(self.layout()?.range_is_free(start, length))
    }
}

impl RangeDigest for PhysicalDisk {
    fn digest(&mut self, parts: &[&[u8]]) -> [u8; 32] {
        crate::digest::fingerprint(parts)
    }
}

impl PhysicalDisk {
    /// SHA-256 of a byte range, read unbuffered from the disk.
    pub fn range_digest(&mut self, offset: u64, length: u64) -> Result<[u8; 32], IoError> {
        let mut hasher = Sha256::new();
        let mut buffer = vec![0u8; LANES * PIECE];
        let mut done = 0u64;
        while done < length {
            let n = (length - done).min(buffer.len() as u64) as usize;
            self.read_at(offset + done, &mut buffer[..n])?;
            hasher.update(&buffer[..n]);
            done += n as u64;
        }
        Ok(hasher.finalize().into())
    }

    /// Measure read throughput: read `[offset, offset + length)` from its start, one piece per
    /// lane at a time (the same reads a move makes), until the range is read or `budget` has
    /// passed. Only reads; the range must be sector aligned.
    pub fn bench(
        &mut self,
        offset: u64,
        length: u64,
        budget: Duration,
    ) -> Result<BenchResult, IoError> {
        let sector = u64::from(self.sector_size);
        let length = length / sector * sector;
        let mut buffer = vec![0u8; LANES * PIECE];
        let started = Instant::now();
        let mut done = 0u64;
        while done < length && started.elapsed() < budget {
            let n = (length - done).min(buffer.len() as u64) as usize;
            self.read_at(offset + done, &mut buffer[..n])?;
            done += n as u64;
        }
        Ok(BenchResult {
            bytes: done,
            elapsed: started.elapsed(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parallel_reads_and_writes_land_in_the_right_place() {
        const MIB: usize = 1 << 20;
        let size = 96 * MIB;
        let path = std::env::temp_dir().join(format!("opm-lanes-{}.bin", std::process::id()));
        std::fs::File::create(&path)
            .and_then(|f| f.set_len(size as u64))
            .unwrap();
        let mut disk =
            PhysicalDisk::open_file_for_test(path.to_str().unwrap(), size as u64).unwrap();
        let pattern = |i: usize, seed: usize| ((i / 4096) * 7 + i * 13 + seed) as u8;

        // Spans many pieces with a partial last piece, at an offset that isn't piece aligned.
        let data: Vec<u8> = (0..(37 * MIB + 8192)).map(|i| pattern(i, 1)).collect();
        disk.write_at(3 * MIB as u64 + 4096, &data).unwrap();
        disk.flush().unwrap();
        let mut back = vec![0u8; data.len()];
        disk.read_at(3 * MIB as u64 + 4096, &mut back).unwrap();
        assert!(back == data, "read back differs");

        // A single small request takes the one-lane path.
        let small: Vec<u8> = (0..8192).map(|i| pattern(i, 2)).collect();
        disk.write_at(0, &small).unwrap();
        let mut back = vec![0u8; 8192];
        disk.read_at(0, &mut back).unwrap();
        assert_eq!(back, small);

        // Seen through an ordinary read of the file, every byte is where it was written.
        drop(disk);
        let file = std::fs::read(&path).unwrap();
        assert_eq!(&file[..8192], &small[..]);
        assert!(file[3 * MIB + 4096..3 * MIB + 4096 + data.len()] == data[..]);
        assert!(file[3 * MIB + 4096 + data.len()..].iter().all(|&b| b == 0));
        std::fs::remove_file(&path).unwrap();
    }

    #[test]
    fn bench_opens_the_disk_for_reading_only() {
        const MIB: usize = 1 << 20;
        let size = 80 * MIB;
        let path = std::env::temp_dir().join(format!("opm-bench-{}.bin", std::process::id()));
        let content: Vec<u8> = (0..size).map(|i| (i * 31 / 4096) as u8).collect();
        std::fs::write(&path, &content).unwrap();

        let mut disk =
            PhysicalDisk::open_file_for_test_with(path.to_str().unwrap(), size as u64, false)
                .unwrap();
        assert!(!disk.is_writable());

        // Reads the whole range when the budget allows, and nothing when there is no budget.
        let all = disk
            .bench(MIB as u64, 70 * MIB as u64, Duration::from_secs(60))
            .unwrap();
        assert_eq!(all.bytes, 70 * MIB as u64);
        assert!(all.bytes_per_second() > 0);
        let none = disk.bench(0, size as u64, Duration::ZERO).unwrap();
        assert_eq!(none.bytes, 0);

        // The disk refuses writes, and no lane's handle has write access, so Windows refuses
        // them too, even past the disk's own check.
        assert_eq!(
            disk.write_at(0, &[0xAA; 4096]),
            Err(IoError::Os(ERROR_ACCESS_DENIED))
        );
        for lane in &mut disk.lanes {
            assert_eq!(
                lane.write(0, &[0xAA; 4096]),
                Err(IoError::Os(ERROR_ACCESS_DENIED))
            );
        }
        drop(disk);

        assert!(
            std::fs::read(&path).unwrap() == content,
            "the bench changed the file"
        );
        std::fs::remove_file(&path).unwrap();
    }

    #[test]
    fn errors_from_any_lane_are_reported() {
        let path = std::env::temp_dir().join(format!("opm-lanes-err-{}.bin", std::process::id()));
        std::fs::File::create(&path)
            .and_then(|f| f.set_len(16 << 20))
            .unwrap();
        // Claims to be bigger than the file: reads past its end fail on some lane.
        let mut disk = PhysicalDisk::open_file_for_test(path.to_str().unwrap(), 64 << 20).unwrap();
        let mut buf = vec![0u8; 48 << 20];
        assert!(disk.read_at(0, &mut buf).is_err());
        drop(disk);
        std::fs::remove_file(&path).unwrap();
    }
}
