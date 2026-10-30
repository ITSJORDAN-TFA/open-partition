//! Journaled, crash-safe block move of a partition (plan Phase 3, gaps G3, G6, G7).
//!
//! The data is copied in chunks no larger than the shift distance, in the direction that never
//! overwrites unread source data. With that rule a chunk never overlaps its own source, so
//! re-copying a chunk after a crash is always safe and no source backup is needed (G3).
//! Each chunk is flushed and read back before the journal checkpoint says it is done (G7).
//! Recovery trusts the live partition table over the journal phase where they can differ (G6).
//! For the whole move the partition's entry is removed from the table, so the OS has no
//! partition, volume or file system on the data while it is in flux and a half-moved volume can
//! never be mounted and written to if the move is interrupted (G9). Some Windows versions also
//! refuse raw writes into any partition's sectors unless its volume is locked, which is
//! impossible once the volume is gone; a removed entry avoids that too (G10). The journal holds
//! a snapshot of the entry, and the final table update puts it back at the target in one write.

use alloc::vec;
use core::fmt;
use core::ops::Range;

use crate::device::{BlockDevice, IoError};
use crate::journal::{
    commit, load_newest, DecodeError, Direction, JournalStore, LoadError, MoveRecord,
    PartitionEntry, PartitionKey, Phase,
};

/// Largest chunk the mover uses. Bigger chunks are faster but hold more data in memory.
pub const MAX_CHUNK_SIZE: u64 = 64 * 1024 * 1024;

/// Access to the partition table of the disk being moved on. On Windows this wraps
/// `IOCTL_DISK_GET/SET_DRIVE_LAYOUT_EX`; in tests it is a simulated table.
pub trait PartitionTable {
    /// Starting byte offset of the partition, or `None` if the table has no such partition.
    fn partition_start(&mut self, partition: PartitionKey) -> Result<Option<u64>, IoError>;

    /// Snapshot of the partition's entry: everything [`attach`](Self::attach) needs to put it
    /// back exactly as it was apart from the start.
    fn entry(&mut self, partition: PartitionKey) -> Result<PartitionEntry, IoError>;

    /// Remove the partition's entry from the table, so the OS drops the partition and any
    /// volume on it. Must be idempotent and durable when it returns.
    fn detach(&mut self, partition: PartitionKey) -> Result<(), IoError>;

    /// In one durable table write: put `entry` back, starting at `start`, with its size and
    /// identity unchanged.
    fn attach(
        &mut self,
        partition: PartitionKey,
        entry: &PartitionEntry,
        start: u64,
    ) -> Result<(), IoError>;

    /// Whether no partition in the table overlaps `[start, start + length)`.
    fn range_is_free(&mut self, start: u64, length: u64) -> Result<bool, IoError>;
}

/// A strong digest (SHA-256 on Windows) of a byte range of the disk. Kept out of the core so
/// it stays dependency-free.
pub trait RangeDigest {
    fn range_digest(&mut self, offset: u64, length: u64) -> Result<[u8; 32], IoError>;
}

/// Reported after each chunk is copied and checkpointed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Progress {
    pub chunks_done: u64,
    pub chunks_total: u64,
    pub bytes_done: u64,
    pub bytes_total: u64,
}

/// A move the caller wants done. Offsets and lengths are in bytes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MoveJob {
    pub operation_id: [u8; 16],
    pub partition: PartitionKey,
    pub source_offset: u64,
    pub target_offset: u64,
    pub length: u64,
    pub chunk_size: u64,
    /// Digest the source before copying and require the target to match before the partition
    /// is revealed. Costs a second full read of the data; strongly recommended.
    pub verify: bool,
}

/// Identity of the disk, recorded in the journal so recovery never replays a journal onto
/// the wrong disk.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DiskIdentity {
    pub id: [u8; 16],
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MoveError {
    Io(IoError),
    /// The job breaks a safety rule; nothing was written.
    InvalidJob(&'static str),
    /// The journal could not be trusted (newer format, or impossible contents).
    Journal(DecodeError),
    /// An unfinished move is already journaled; it must be recovered first.
    PendingOperation,
    /// The journal belongs to a different disk.
    WrongDisk,
    /// The partition table does not show the partition where the journal says it must be.
    UnexpectedLayout {
        expected: u64,
        found: Option<u64>,
    },
    /// A chunk read back from the target did not match what was written.
    VerifyFailed {
        offset: u64,
    },
    /// Another partition was created over the data being moved while its entry was out of
    /// the table. Nothing more is written.
    RangeInUse {
        offset: u64,
    },
}

impl fmt::Display for MoveError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            MoveError::Io(e) => write!(f, "disk I/O failed: {e}"),
            MoveError::InvalidJob(why) => write!(f, "move refused: {why}"),
            MoveError::Journal(e) => write!(f, "journal unusable: {e}"),
            MoveError::PendingOperation => {
                f.write_str("an unfinished move must be recovered first")
            }
            MoveError::WrongDisk => f.write_str("the journal belongs to a different disk"),
            MoveError::UnexpectedLayout {
                expected,
                found: Some(found),
            } => {
                write!(
                    f,
                    "partition expected at byte {expected} but the table says {found}"
                )
            }
            MoveError::UnexpectedLayout {
                expected,
                found: None,
            } => {
                write!(
                    f,
                    "partition expected at byte {expected} but it is not in the table"
                )
            }
            MoveError::VerifyFailed { offset } => {
                write!(f, "verification failed for data at byte {offset}")
            }
            MoveError::RangeInUse { offset } => {
                write!(f, "another partition now overlaps the data being moved at byte {offset}; it was created while the move was unfinished")
            }
        }
    }
}

impl From<IoError> for MoveError {
    fn from(e: IoError) -> Self {
        MoveError::Io(e)
    }
}

impl From<LoadError> for MoveError {
    fn from(e: LoadError) -> Self {
        match e {
            LoadError::Io(e) => MoveError::Io(e),
            LoadError::Decode(e) => MoveError::Journal(e),
        }
    }
}

/// The chunk size the mover should use for a shift: as large as allowed, but never more than
/// the shift itself (G3) or [`MAX_CHUNK_SIZE`].
pub fn default_chunk_size(source_offset: u64, target_offset: u64, sector_size: u32) -> u64 {
    let shift = source_offset.abs_diff(target_offset);
    let sector = u64::from(sector_size);
    let chunk = shift.min(MAX_CHUNK_SIZE);
    (chunk / sector * sector).max(sector)
}

fn overlaps(a: &Range<u64>, b: &Range<u64>) -> bool {
    a.start < b.end && b.start < a.end
}

/// Check every safety rule for a job without touching the disk. `reserved` lists byte ranges
/// on this disk that must not be written (e.g. a journal stored on the same disk).
pub fn validate<D: BlockDevice>(
    job: &MoveJob,
    disk: &D,
    reserved: &[Range<u64>],
) -> Result<Direction, MoveError> {
    let sector = u64::from(disk.sector_size());
    let aligned = |v: u64| v % sector == 0;
    if job.length == 0 {
        return Err(MoveError::InvalidJob("length is zero"));
    }
    if !(aligned(job.source_offset)
        && aligned(job.target_offset)
        && aligned(job.length)
        && aligned(job.chunk_size))
    {
        return Err(MoveError::InvalidJob(
            "offsets, length and chunk size must be sector aligned",
        ));
    }
    if job.source_offset == job.target_offset {
        return Err(MoveError::InvalidJob("source and target are the same"));
    }
    let shift = job.source_offset.abs_diff(job.target_offset);
    if job.chunk_size == 0 || job.chunk_size > shift || job.chunk_size > MAX_CHUNK_SIZE {
        return Err(MoveError::InvalidJob(
            "chunk size must be between one sector and the shift distance",
        ));
    }
    let end = |start: u64| {
        start
            .checked_add(job.length)
            .filter(|&e| e <= disk.size_bytes())
    };
    let (Some(source_end), Some(target_end)) = (end(job.source_offset), end(job.target_offset))
    else {
        return Err(MoveError::InvalidJob(
            "source or target range runs past the end of the disk",
        ));
    };
    let touched = [job.source_offset..source_end, job.target_offset..target_end];
    if reserved
        .iter()
        .any(|r| touched.iter().any(|t| overlaps(r, t)))
    {
        return Err(MoveError::InvalidJob(
            "source or target range overlaps a reserved region such as the journal",
        ));
    }
    Ok(if job.target_offset > job.source_offset {
        Direction::Backward
    } else {
        Direction::Forward
    })
}

/// Start a move: journal it, copy it, update the partition table, and mark it done.
///
/// If this returns an error (including a crash), the disk is left in a state that
/// [`recover`] will finish.
pub fn start_move<D, J>(
    disk: &mut D,
    journal: &mut J,
    identity: DiskIdentity,
    job: &MoveJob,
    reserved: &[Range<u64>],
) -> Result<MoveRecord, MoveError>
where
    D: BlockDevice + PartitionTable + RangeDigest,
    J: JournalStore,
{
    start_move_with_progress(disk, journal, identity, job, reserved, &mut |_| {})
}

/// [`start_move`] that calls `progress` after every checkpointed chunk.
pub fn start_move_with_progress<D, J>(
    disk: &mut D,
    journal: &mut J,
    identity: DiskIdentity,
    job: &MoveJob,
    reserved: &[Range<u64>],
    progress: &mut dyn FnMut(Progress),
) -> Result<MoveRecord, MoveError>
where
    D: BlockDevice + PartitionTable + RangeDigest,
    J: JournalStore,
{
    let direction = validate(job, disk, reserved)?;
    let previous = load_newest(journal)?;
    if previous.is_some_and(|r| r.phase != Phase::Done) {
        return Err(MoveError::PendingOperation);
    }
    match disk.partition_start(job.partition)? {
        Some(start) if start == job.source_offset => {}
        found => {
            return Err(MoveError::UnexpectedLayout {
                expected: job.source_offset,
                found,
            })
        }
    }
    let entry = disk.entry(job.partition)?;
    let source_digest = if job.verify {
        Some(disk.range_digest(job.source_offset, job.length)?)
    } else {
        None
    };

    let record = MoveRecord {
        sequence: previous.map_or(1, |r| r.sequence + 1),
        operation_id: job.operation_id,
        disk_id: identity.id,
        disk_size: disk.size_bytes(),
        sector_size: disk.sector_size(),
        partition: job.partition,
        entry,
        source_offset: job.source_offset,
        target_offset: job.target_offset,
        length: job.length,
        chunk_size: job.chunk_size,
        direction,
        phase: Phase::Copying,
        chunks_done: 0,
        source_digest,
    };
    // The journal must be durable before the table or any data is touched.
    commit(journal, &record)?;
    run(disk, journal, record, progress)
}

// Returned once per recovery, so the size of the record does not matter.
#[allow(clippy::large_enum_variant)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Recovery {
    /// No unfinished move was journaled.
    NothingToDo,
    /// An unfinished move was found and completed.
    Completed(MoveRecord),
}

/// Finish any journaled move on this disk. Safe to call on every start-up, and safe to
/// interrupt: calling it again after another crash finishes the job.
pub fn recover<D, J>(
    disk: &mut D,
    journal: &mut J,
    identity: DiskIdentity,
) -> Result<Recovery, MoveError>
where
    D: BlockDevice + PartitionTable + RangeDigest,
    J: JournalStore,
{
    recover_with_progress(disk, journal, identity, &mut |_| {})
}

/// [`recover`] that calls `progress` after every checkpointed chunk.
pub fn recover_with_progress<D, J>(
    disk: &mut D,
    journal: &mut J,
    identity: DiskIdentity,
    progress: &mut dyn FnMut(Progress),
) -> Result<Recovery, MoveError>
where
    D: BlockDevice + PartitionTable + RangeDigest,
    J: JournalStore,
{
    let Some(record) = load_newest(journal)? else {
        return Ok(Recovery::NothingToDo);
    };
    if record.phase == Phase::Done {
        return Ok(Recovery::NothingToDo);
    }
    if record.disk_id != identity.id
        || record.disk_size != disk.size_bytes()
        || record.sector_size != disk.sector_size()
    {
        return Err(MoveError::WrongDisk);
    }
    run(disk, journal, record, progress).map(Recovery::Completed)
}

/// Byte range of chunk `index` relative to the start of the partition.
fn chunk_range(record: &MoveRecord, index: u64) -> (u64, u64) {
    match record.direction {
        Direction::Forward => {
            let start = index * record.chunk_size;
            (start, record.chunk_size.min(record.length - start))
        }
        Direction::Backward => {
            let end = record.length - index * record.chunk_size;
            let start = end.saturating_sub(record.chunk_size);
            (start, end - start)
        }
    }
}

fn expect_start<D: PartitionTable>(
    disk: &mut D,
    partition: PartitionKey,
    expected: u64,
) -> Result<(), MoveError> {
    match disk.partition_start(partition)? {
        Some(start) if start == expected => Ok(()),
        found => Err(MoveError::UnexpectedLayout { expected, found }),
    }
}

/// While the entry is out of the table, no other partition may claim the source or target.
fn expect_free<D: PartitionTable>(disk: &mut D, record: &MoveRecord) -> Result<(), MoveError> {
    for offset in [record.source_offset, record.target_offset] {
        if !disk.range_is_free(offset, record.length)? {
            return Err(MoveError::RangeInUse { offset });
        }
    }
    Ok(())
}

fn advance<J: JournalStore>(journal: &mut J, record: &mut MoveRecord) -> Result<(), MoveError> {
    record.sequence += 1;
    commit(journal, record)?;
    Ok(())
}

/// Drive a journaled move from whatever phase it is in to `Done`.
fn run<D, J>(
    disk: &mut D,
    journal: &mut J,
    mut record: MoveRecord,
    progress: &mut dyn FnMut(Progress),
) -> Result<MoveRecord, MoveError>
where
    D: BlockDevice + PartitionTable + RangeDigest,
    J: JournalStore,
{
    let chunks = record.length.div_ceil(record.chunk_size);
    loop {
        match record.phase {
            Phase::Copying => {
                match disk.partition_start(record.partition)? {
                    Some(start) if start == record.source_offset => {
                        disk.detach(record.partition)?
                    }
                    None => {}
                    found => {
                        return Err(MoveError::UnexpectedLayout {
                            expected: record.source_offset,
                            found,
                        })
                    }
                }
                expect_free(disk, &record)?;
                let size = usize::try_from(record.chunk_size)
                    .map_err(|_| MoveError::Journal(DecodeError::Corrupt))?;
                let mut data = vec![0u8; size];
                let mut check = vec![0u8; size];
                while record.chunks_done < chunks {
                    let (offset, len) = chunk_range(&record, record.chunks_done);
                    let len = len as usize;
                    let source = record.source_offset + offset;
                    let target = record.target_offset + offset;
                    disk.read_at(source, &mut data[..len])?;
                    disk.write_at(target, &data[..len])?;
                    disk.flush()?;
                    disk.read_at(target, &mut check[..len])?;
                    if data[..len] != check[..len] {
                        return Err(MoveError::VerifyFailed { offset: target });
                    }
                    record.chunks_done += 1;
                    if record.chunks_done == chunks {
                        record.phase = Phase::CopyComplete;
                    }
                    advance(journal, &mut record)?;
                    progress(Progress {
                        chunks_done: record.chunks_done,
                        chunks_total: chunks,
                        bytes_done: (record.chunks_done * record.chunk_size).min(record.length),
                        bytes_total: record.length,
                    });
                }
                if record.phase == Phase::Copying {
                    // Only reachable with a hand-made record; keep the state machine total.
                    record.phase = Phase::CopyComplete;
                    advance(journal, &mut record)?;
                }
            }
            Phase::CopyComplete => {
                // A crash may have hit after the table write but before the journal caught up (G6).
                match disk.partition_start(record.partition)? {
                    None => {
                        expect_free(disk, &record)?;
                        // Last chance to catch a bad copy: the entry is still out of the table.
                        if let Some(expected) = record.source_digest {
                            if disk.range_digest(record.target_offset, record.length)? != expected {
                                return Err(MoveError::VerifyFailed {
                                    offset: record.target_offset,
                                });
                            }
                        }
                        disk.attach(record.partition, &record.entry, record.target_offset)?;
                    }
                    Some(start) if start == record.target_offset => {}
                    found => {
                        return Err(MoveError::UnexpectedLayout {
                            expected: record.target_offset,
                            found,
                        })
                    }
                }
                record.phase = Phase::TableUpdated;
                advance(journal, &mut record)?;
            }
            Phase::TableUpdated => {
                expect_start(disk, record.partition, record.target_offset)?;
                record.phase = Phase::Done;
                advance(journal, &mut record)?;
            }
            Phase::Done => return Ok(record),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct FakeDisk;

    impl BlockDevice for FakeDisk {
        fn sector_size(&self) -> u32 {
            512
        }
        fn size_bytes(&self) -> u64 {
            1 << 20
        }
        fn read_at(&mut self, _: u64, _: &mut [u8]) -> Result<(), IoError> {
            unreachable!()
        }
        fn write_at(&mut self, _: u64, _: &[u8]) -> Result<(), IoError> {
            unreachable!()
        }
        fn flush(&mut self) -> Result<(), IoError> {
            unreachable!()
        }
    }

    fn job(source: u64, target: u64, length: u64, chunk: u64) -> MoveJob {
        MoveJob {
            operation_id: [0; 16],
            partition: PartitionKey([0; 16]),
            source_offset: source,
            target_offset: target,
            length,
            chunk_size: chunk,
            verify: false,
        }
    }

    #[test]
    fn direction_follows_the_shift() {
        assert_eq!(
            validate(&job(4096, 8192, 4096, 4096), &FakeDisk, &[]),
            Ok(Direction::Backward)
        );
        assert_eq!(
            validate(&job(8192, 4096, 4096, 4096), &FakeDisk, &[]),
            Ok(Direction::Forward)
        );
    }

    #[test]
    fn chunk_larger_than_shift_is_refused() {
        assert!(matches!(
            validate(&job(4096, 4608, 8192, 1024), &FakeDisk, &[]),
            Err(MoveError::InvalidJob(_))
        ));
        assert!(validate(&job(4096, 4608, 8192, 512), &FakeDisk, &[]).is_ok());
    }

    #[test]
    fn misaligned_past_end_and_reserved_overlap_are_refused() {
        let refused = |j: MoveJob, reserved: &[Range<u64>]| {
            matches!(
                validate(&j, &FakeDisk, reserved),
                Err(MoveError::InvalidJob(_))
            )
        };
        assert!(refused(job(100, 4096, 4096, 512), &[]));
        assert!(refused(job(4096, (1 << 20) - 2048, 4096, 512), &[]));
        assert!(refused(job(4096, 4096, 4096, 512), &[]));
        assert!(refused(
            job(4096, 16384, 4096, 4096),
            &[Range {
                start: 17920,
                end: 18432
            }]
        ));
        assert!(!refused(
            job(4096, 16384, 4096, 4096),
            &[Range {
                start: 0,
                end: 4096
            }]
        ));
    }

    #[test]
    fn default_chunk_respects_shift_and_maximum() {
        assert_eq!(default_chunk_size(0, 1 << 20, 512), 1 << 20);
        assert_eq!(default_chunk_size(1 << 30, 0, 4096), MAX_CHUNK_SIZE);
        assert_eq!(default_chunk_size(0, 4096 + 512, 4096), 4096);
    }

    #[test]
    fn backward_chunks_cover_the_range_from_the_end() {
        let record = MoveRecord {
            sequence: 1,
            operation_id: [0; 16],
            disk_id: [0; 16],
            disk_size: 0,
            sector_size: 512,
            partition: PartitionKey([0; 16]),
            entry: PartitionEntry([0; crate::journal::ENTRY_SIZE]),
            source_offset: 0,
            target_offset: 0,
            length: 2500,
            chunk_size: 1000,
            direction: Direction::Backward,
            phase: Phase::Copying,
            chunks_done: 0,
            source_digest: None,
        };
        assert_eq!(chunk_range(&record, 0), (1500, 1000));
        assert_eq!(chunk_range(&record, 1), (500, 1000));
        assert_eq!(chunk_range(&record, 2), (0, 500));
        let forward = MoveRecord {
            direction: Direction::Forward,
            ..record
        };
        assert_eq!(chunk_range(&forward, 2), (2000, 500));
    }
}
