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
//!
//! Only the parts of the partition that hold data are hashed and copied ([`CopyMap`]). The
//! source is hashed chunk by chunk once the entry is out of the table, and each chunk is checked
//! against its hash as it is copied, so data that changes under the move is caught at once.

use alloc::vec;
use alloc::vec::Vec;
use core::fmt;
use core::ops::Range;

use crate::device::{BlockDevice, IoError};
use crate::journal::{
    commit, load_newest, DecodeError, Direction, JournalStore, LoadError, MoveRecord,
    PartitionEntry, PartitionKey, Phase,
};

/// Largest chunk the mover uses. Each chunk is flushed, read back and checkpointed, so bigger
/// chunks are faster, but the mover holds two of them in memory.
pub const MAX_CHUNK_SIZE: u64 = 128 * 1024 * 1024;

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

    /// Whether no partition in the table overlaps `[start, start + length)`, as a place for
    /// `partition`. A partition that contains it (the extended partition around a logical
    /// drive) doesn't count; whatever else is inside that one does.
    fn range_is_free(
        &mut self,
        partition: PartitionKey,
        start: u64,
        length: u64,
    ) -> Result<bool, IoError>;
}

/// A strong digest (SHA-256 on Windows) of data the mover has read. Kept out of the core so it
/// stays dependency-free.
pub trait RangeDigest {
    /// Digest of `parts`, concatenated.
    fn digest(&mut self, parts: &[&[u8]]) -> [u8; 32];
}

/// The parts of a partition that hold data, as sorted, non-overlapping, sector-aligned
/// `(start, length)` byte ranges relative to the partition start. Only these are hashed and
/// copied; the rest is free space whose contents don't matter (free NTFS clusters).
/// `digest` identifies the map in the journal so recovery copies exactly the same parts.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CopyMap {
    pub ranges: Vec<(u64, u64)>,
    pub digest: Option<[u8; 32]>,
}

impl CopyMap {
    /// Copy everything.
    pub fn full(length: u64) -> Self {
        Self {
            ranges: vec![(0, length)],
            digest: None,
        }
    }

    /// Bytes the map covers.
    pub fn bytes(&self) -> u64 {
        self.ranges.iter().map(|r| r.1).sum()
    }

    /// The mapped parts of `[start, start + len)`, clipped to it.
    pub fn parts(&self, start: u64, len: u64) -> impl Iterator<Item = (u64, u64)> + '_ {
        let end = start + len;
        let first = self.ranges.partition_point(|&(s, l)| s + l <= start);
        self.ranges[first..]
            .iter()
            .take_while(move |&&(s, _)| s < end)
            .map(move |&(s, l)| {
                let a = s.max(start);
                (a, (s + l).min(end) - a)
            })
    }

    fn check(&self, length: u64, sector: u64) -> Result<(), MoveError> {
        let mut end = 0;
        for &(s, l) in &self.ranges {
            if l == 0 || s < end || s % sector != 0 || l % sector != 0 || s + l > length {
                return Err(MoveError::InvalidJob(
                    "copy map ranges must be sorted, sector aligned and inside the partition",
                ));
            }
            end = s + l;
        }
        Ok(())
    }
}

/// What the mover is doing when it reports progress.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Stage {
    /// Reading the source to take its digest, before anything is copied.
    Hashing,
    /// Copying chunks to the target.
    Copying,
    /// Reading the finished copy back to compare it with the source digest (only after a
    /// restart; otherwise each chunk was already checked as it was copied).
    Verifying,
}

/// Reported after each chunk is hashed, copied or verified.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Progress {
    pub stage: Stage,
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
    /// A chunk read back from the target did not match what was written, or the finished
    /// copy does not match the source digest.
    VerifyFailed {
        offset: u64,
    },
    /// The source changed after it was hashed: something wrote to the disk under the move.
    SourceChanged {
        offset: u64,
    },
    /// The copy map given to recovery is not the one the move was journaled with.
    MapMismatch,
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
            MoveError::SourceChanged { offset } => {
                write!(f, "the data at byte {offset} changed after it was hashed, while the move was running: something is writing to the disk underneath the file system")
            }
            MoveError::MapMismatch => {
                f.write_str("the copy map does not match the one the move was journaled with")
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
    let map = CopyMap::full(job.length);
    start_move_with_progress(disk, journal, identity, job, reserved, &map, &mut |_| {})
}

/// [`start_move`] that copies only the parts in `map` and calls `progress` as it goes.
pub fn start_move_with_progress<D, J>(
    disk: &mut D,
    journal: &mut J,
    identity: DiskIdentity,
    job: &MoveJob,
    reserved: &[Range<u64>],
    map: &CopyMap,
    progress: &mut dyn FnMut(Progress),
) -> Result<MoveRecord, MoveError>
where
    D: BlockDevice + PartitionTable + RangeDigest,
    J: JournalStore,
{
    let direction = validate(job, disk, reserved)?;
    map.check(job.length, u64::from(disk.sector_size()))?;
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
        verify: job.verify,
        source_digest: None,
        map_digest: map.digest,
    };
    // The journal must be durable before the table or any data is touched.
    commit(journal, &record)?;
    run(disk, journal, record, map, progress)
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
    let Some(record) = load_newest(journal)? else {
        return Ok(Recovery::NothingToDo);
    };
    let map = CopyMap::full(record.length);
    recover_with_progress(disk, journal, identity, &map, &mut |_| {})
}

/// [`recover`] with the copy map the move was started with (the whole partition if it had
/// none), calling `progress` as it goes.
pub fn recover_with_progress<D, J>(
    disk: &mut D,
    journal: &mut J,
    identity: DiskIdentity,
    map: &CopyMap,
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
    run(disk, journal, record, map, progress).map(Recovery::Completed)
}

/// Undo an unfinished move instead of finishing it: put the partition's entry back at its
/// source, as it was before the move. Only possible while the source has never been written,
/// which is the case when source and target do not overlap. Use it when finishing is
/// impossible, such as when the copy fails verification.
///
/// Safe to interrupt and call again. The final (`Done`) record has the source as its target. Refuses with [`MoveError::UnexpectedLayout`] if the entry is already at the target:
/// the move is then complete apart from bookkeeping and must be finished with [`recover`].
pub fn roll_back<D, J>(
    disk: &mut D,
    journal: &mut J,
    identity: DiskIdentity,
) -> Result<Recovery, MoveError>
where
    D: BlockDevice + PartitionTable,
    J: JournalStore,
{
    let Some(mut record) = load_newest(journal)?.filter(|r| r.phase != Phase::Done) else {
        return Ok(Recovery::NothingToDo);
    };
    if record.disk_id != identity.id
        || record.disk_size != disk.size_bytes()
        || record.sector_size != disk.sector_size()
    {
        return Err(MoveError::WrongDisk);
    }
    if record.source_offset.abs_diff(record.target_offset) < record.length {
        return Err(MoveError::InvalidJob(
            "the move overlaps its source, so part of the original data has been overwritten; it can only be finished",
        ));
    }
    match disk.partition_start(record.partition)? {
        Some(start) if start == record.source_offset => {}
        None => {
            if !disk.range_is_free(record.partition, record.source_offset, record.length)? {
                return Err(MoveError::RangeInUse {
                    offset: record.source_offset,
                });
            }
            disk.attach(record.partition, &record.entry, record.source_offset)?;
        }
        found => {
            return Err(MoveError::UnexpectedLayout {
                expected: record.source_offset,
                found,
            })
        }
    }
    record.target_offset = record.source_offset;
    record.phase = Phase::Done;
    advance(journal, &mut record)?;
    Ok(Recovery::Completed(record))
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
        if !disk.range_is_free(record.partition, offset, record.length)? {
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

/// Read the mapped parts of chunk `[offset, offset + len)` (relative to the partition) from the
/// partition starting at `base` into `data`, at their positions within the chunk.
fn read_parts<D: BlockDevice>(
    disk: &mut D,
    map: &CopyMap,
    base: u64,
    offset: u64,
    len: u64,
    data: &mut [u8],
) -> Result<(), IoError> {
    for (s, l) in map.parts(offset, len) {
        let at = (s - offset) as usize;
        disk.read_at(base + s, &mut data[at..at + l as usize])?;
    }
    Ok(())
}

/// Digest of the mapped parts of a chunk already read into `data`.
fn digest_parts<D: RangeDigest>(
    disk: &mut D,
    map: &CopyMap,
    offset: u64,
    len: u64,
    data: &[u8],
) -> [u8; 32] {
    let parts: Vec<&[u8]> = map
        .parts(offset, len)
        .map(|(s, l)| {
            let at = (s - offset) as usize;
            &data[at..at + l as usize]
        })
        .collect();
    disk.digest(&parts)
}

/// Per-chunk digests of the partition starting at `base`, and the digest of them all.
fn digest_chunks<D: BlockDevice + RangeDigest>(
    disk: &mut D,
    record: &MoveRecord,
    map: &CopyMap,
    base: u64,
    stage: Stage,
    data: &mut [u8],
    progress: &mut dyn FnMut(Progress),
) -> Result<(Vec<[u8; 32]>, [u8; 32]), IoError> {
    let chunks = record.length.div_ceil(record.chunk_size);
    let mut digests = Vec::with_capacity(chunks as usize);
    for index in 0..chunks {
        let (offset, len) = chunk_range(record, index);
        read_parts(disk, map, base, offset, len, data)?;
        digests.push(digest_parts(disk, map, offset, len, data));
        progress(Progress {
            stage,
            chunks_done: index + 1,
            chunks_total: chunks,
            bytes_done: ((index + 1) * record.chunk_size).min(record.length),
            bytes_total: record.length,
        });
    }
    let all: Vec<u8> = digests.iter().flatten().copied().collect();
    let whole = disk.digest(&[&all]);
    Ok((digests, whole))
}

/// Drive a journaled move from whatever phase it is in to `Done`.
fn run<D, J>(
    disk: &mut D,
    journal: &mut J,
    mut record: MoveRecord,
    map: &CopyMap,
    progress: &mut dyn FnMut(Progress),
) -> Result<MoveRecord, MoveError>
where
    D: BlockDevice + PartitionTable + RangeDigest,
    J: JournalStore,
{
    if record.map_digest != map.digest {
        return Err(MoveError::MapMismatch);
    }
    map.check(record.length, u64::from(record.sector_size))?;
    let chunks = record.length.div_ceil(record.chunk_size);
    let size =
        usize::try_from(record.chunk_size).map_err(|_| MoveError::Journal(DecodeError::Corrupt))?;
    let mut data = vec![0u8; size];
    let mut check = vec![0u8; size];
    // Digests of every source chunk, taken in this run before the first chunk was copied. When
    // present, every chunk was checked against them as it was copied, so the finished copy
    // needs no second read.
    let mut chunk_digests: Option<Vec<[u8; 32]>> = None;
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
                if record.verify && record.source_digest.is_none() {
                    // Hashed only now that the OS has no partition, volume or file system on
                    // the source: nothing it does on the way out can change the data after this.
                    if record.chunks_done != 0 {
                        return Err(MoveError::Journal(DecodeError::Corrupt));
                    }
                    let (digests, whole) = digest_chunks(
                        disk,
                        &record,
                        map,
                        record.source_offset,
                        Stage::Hashing,
                        &mut data,
                        progress,
                    )?;
                    record.source_digest = Some(whole);
                    advance(journal, &mut record)?;
                    chunk_digests = Some(digests);
                }
                while record.chunks_done < chunks {
                    let index = record.chunks_done;
                    let (offset, len) = chunk_range(&record, index);
                    read_parts(disk, map, record.source_offset, offset, len, &mut data)?;
                    if let Some(digests) = &chunk_digests {
                        if digest_parts(disk, map, offset, len, &data) != digests[index as usize] {
                            return Err(MoveError::SourceChanged {
                                offset: record.source_offset + offset,
                            });
                        }
                    }
                    let mut wrote = false;
                    for (s, l) in map.parts(offset, len) {
                        let at = (s - offset) as usize;
                        disk.write_at(record.target_offset + s, &data[at..at + l as usize])?;
                        wrote = true;
                    }
                    if wrote {
                        disk.flush()?;
                        read_parts(disk, map, record.target_offset, offset, len, &mut check)?;
                        for (s, l) in map.parts(offset, len) {
                            let range = (s - offset) as usize..(s - offset + l) as usize;
                            if data[range.clone()] != check[range] {
                                return Err(MoveError::VerifyFailed {
                                    offset: record.target_offset + s,
                                });
                            }
                        }
                    }
                    record.chunks_done += 1;
                    if record.chunks_done == chunks {
                        record.phase = Phase::CopyComplete;
                    }
                    advance(journal, &mut record)?;
                    progress(Progress {
                        stage: Stage::Copying,
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
                        // Not needed if every chunk was checked against its digest in this run.
                        match record.source_digest {
                            Some(expected) if chunk_digests.is_none() => {
                                let (_, whole) = digest_chunks(
                                    disk,
                                    &record,
                                    map,
                                    record.target_offset,
                                    Stage::Verifying,
                                    &mut data,
                                    progress,
                                )?;
                                if whole != expected {
                                    return Err(MoveError::VerifyFailed {
                                        offset: record.target_offset,
                                    });
                                }
                            }
                            None if record.verify => {
                                return Err(MoveError::Journal(DecodeError::Corrupt))
                            }
                            _ => {}
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
            verify: false,
            source_digest: None,
            map_digest: None,
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
