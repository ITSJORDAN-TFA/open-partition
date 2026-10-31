//! Crash-safety journal for partition moves (plan section 5, handover section 5).
//!
//! The journal is two fixed-size slots. Each commit writes the *other* slot with a higher
//! sequence number and then flushes, so a torn write can only damage the slot being written;
//! the previous record in the other slot stays valid. On start-up the newest record whose
//! checksum is valid wins.

use core::fmt;

use crate::crc32::crc32;
use crate::device::{BlockDevice, IoError};

/// Size of one journal slot. A multiple of every supported sector size (512 and 4096).
pub const RECORD_SIZE: usize = 4096;

const MAGIC: [u8; 8] = *b"OPMJRNL\0";
/// Bump when the record layout changes. Older engines refuse newer journals instead of
/// misreading them.
pub const FORMAT_VERSION: u16 = 4;

const CRC_OFFSET: usize = RECORD_SIZE - 4;
const ENTRY_OFFSET: usize = 176;
const FLAGS_OFFSET: usize = ENTRY_OFFSET + ENTRY_SIZE;
const MAP_DIGEST_OFFSET: usize = FLAGS_OFFSET + 2;

/// Opaque identity of the partition being moved: the GPT partition GUID, or an MBR key the
/// platform layer chooses. The core only compares and stores it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PartitionKey(pub [u8; 16]);

/// Size of a [`PartitionEntry`] snapshot. Large enough for an MBR extended partition together
/// with the entries of the logical drives inside it, which move as one block.
pub const ENTRY_SIZE: usize = 3584;

/// Opaque snapshot of the partition's table entries (type, attributes, name, flags...), taken
/// before the move. The entries are removed from the table for the whole move and put back
/// from this snapshot at the new position (gaps G9, G10). The platform layer chooses the format.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PartitionEntry(pub [u8; ENTRY_SIZE]);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Direction {
    /// Moving left (towards the start of the disk): copy from the first chunk forwards.
    Forward = 1,
    /// Moving right: copy from the last chunk backwards.
    Backward = 2,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
#[repr(u8)]
pub enum Phase {
    /// The partition's entry is removed from the table (or about to be) and chunks are being
    /// copied; `chunks_done` chunks are copied, flushed and read back.
    Copying = 1,
    /// Every chunk is copied and verified; the entry is not yet back in the table.
    CopyComplete = 2,
    /// The entry is back in the table, at the target.
    TableUpdated = 3,
    /// Nothing left to do. A `Done` record is ignored by recovery.
    Done = 4,
}

/// Everything recovery needs to finish or verify a move.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MoveRecord {
    pub sequence: u64,
    pub operation_id: [u8; 16],
    /// Disk identity (GPT disk GUID, or MBR signature padded with zeros).
    pub disk_id: [u8; 16],
    pub disk_size: u64,
    pub sector_size: u32,
    pub partition: PartitionKey,
    /// The partition's table entry before the move; put back at the target by the final
    /// table update.
    pub entry: PartitionEntry,
    pub source_offset: u64,
    pub target_offset: u64,
    pub length: u64,
    pub chunk_size: u64,
    pub direction: Direction,
    pub phase: Phase,
    pub chunks_done: u64,
    /// Whether the copy is verified against a digest of the source (plan section 6).
    pub verify: bool,
    /// Digest of the source's mapped parts, taken once the entry is out of the table and before
    /// any chunk is copied, so nothing can change the source after it. `None` until then.
    /// The target must match it before the partition is revealed.
    pub source_digest: Option<[u8; 32]>,
    /// Identifies the copy map (the parts of the partition that hold data), so recovery
    /// copies exactly the same parts. `None` means the whole partition.
    pub map_digest: Option<[u8; 32]>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeError {
    /// Wrong magic or checksum: an empty, torn or foreign slot. Treated as "no record".
    Invalid,
    /// A valid record from a newer engine. Must stop recovery, never be ignored.
    UnsupportedVersion(u16),
    /// Checksum is fine but a field holds an impossible value.
    Corrupt,
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DecodeError::Invalid => f.write_str("no valid journal record"),
            DecodeError::UnsupportedVersion(v) => write!(
                f,
                "journal format version {v} is newer than this engine supports"
            ),
            DecodeError::Corrupt => {
                f.write_str("journal record has a valid checksum but invalid contents")
            }
        }
    }
}

impl MoveRecord {
    pub fn encode(&self, out: &mut [u8; RECORD_SIZE]) {
        out.fill(0);
        out[0..8].copy_from_slice(&MAGIC);
        out[8..10].copy_from_slice(&FORMAT_VERSION.to_le_bytes());
        out[10] = self.phase as u8;
        out[11] = self.direction as u8;
        out[12..16].copy_from_slice(&self.sector_size.to_le_bytes());
        out[16..24].copy_from_slice(&self.sequence.to_le_bytes());
        out[24..40].copy_from_slice(&self.operation_id);
        out[40..56].copy_from_slice(&self.disk_id);
        out[56..64].copy_from_slice(&self.disk_size.to_le_bytes());
        out[64..72].copy_from_slice(&self.source_offset.to_le_bytes());
        out[72..80].copy_from_slice(&self.target_offset.to_le_bytes());
        out[80..88].copy_from_slice(&self.length.to_le_bytes());
        out[88..96].copy_from_slice(&self.chunk_size.to_le_bytes());
        out[96..104].copy_from_slice(&self.chunks_done.to_le_bytes());
        out[104..120].copy_from_slice(&self.partition.0);
        if let Some(digest) = self.source_digest {
            out[136] = 1;
            out[137..169].copy_from_slice(&digest);
        }
        out[ENTRY_OFFSET..ENTRY_OFFSET + ENTRY_SIZE].copy_from_slice(&self.entry.0);
        out[FLAGS_OFFSET] = u8::from(self.verify);
        if let Some(digest) = self.map_digest {
            out[FLAGS_OFFSET + 1] = 1;
            out[MAP_DIGEST_OFFSET..MAP_DIGEST_OFFSET + 32].copy_from_slice(&digest);
        }
        let crc = crc32(&out[..CRC_OFFSET]);
        out[CRC_OFFSET..].copy_from_slice(&crc.to_le_bytes());
    }

    pub fn decode(buf: &[u8; RECORD_SIZE]) -> Result<MoveRecord, DecodeError> {
        let u16_at = |i: usize| u16::from_le_bytes([buf[i], buf[i + 1]]);
        let u32_at = |i: usize| u32::from_le_bytes(buf[i..i + 4].try_into().unwrap_or([0; 4]));
        let u64_at = |i: usize| u64::from_le_bytes(buf[i..i + 8].try_into().unwrap_or([0; 8]));
        let bytes16 = |i: usize| -> [u8; 16] { buf[i..i + 16].try_into().unwrap_or([0; 16]) };

        if buf[0..8] != MAGIC || u32_at(CRC_OFFSET) != crc32(&buf[..CRC_OFFSET]) {
            return Err(DecodeError::Invalid);
        }
        let version = u16_at(8);
        if version != FORMAT_VERSION {
            return Err(DecodeError::UnsupportedVersion(version));
        }
        let phase = match buf[10] {
            1 => Phase::Copying,
            2 => Phase::CopyComplete,
            3 => Phase::TableUpdated,
            4 => Phase::Done,
            _ => return Err(DecodeError::Corrupt),
        };
        let direction = match buf[11] {
            1 => Direction::Forward,
            2 => Direction::Backward,
            _ => return Err(DecodeError::Corrupt),
        };
        Ok(MoveRecord {
            sequence: u64_at(16),
            operation_id: bytes16(24),
            disk_id: bytes16(40),
            disk_size: u64_at(56),
            sector_size: u32_at(12),
            partition: PartitionKey(bytes16(104)),
            entry: PartitionEntry(
                buf[ENTRY_OFFSET..ENTRY_OFFSET + ENTRY_SIZE]
                    .try_into()
                    .map_err(|_| DecodeError::Corrupt)?,
            ),
            source_offset: u64_at(64),
            target_offset: u64_at(72),
            length: u64_at(80),
            chunk_size: u64_at(88),
            direction,
            phase,
            chunks_done: u64_at(96),
            verify: match buf[FLAGS_OFFSET] {
                0 => false,
                1 => true,
                _ => return Err(DecodeError::Corrupt),
            },
            source_digest: match buf[136] {
                0 => None,
                1 => buf[137..169].try_into().ok(),
                _ => return Err(DecodeError::Corrupt),
            },
            map_digest: match buf[FLAGS_OFFSET + 1] {
                0 => None,
                1 => buf[MAP_DIGEST_OFFSET..MAP_DIGEST_OFFSET + 32]
                    .try_into()
                    .ok(),
                _ => return Err(DecodeError::Corrupt),
            },
        })
    }
}

/// Where the two journal slots live. Implementations must make `write_slot` durable only
/// after `flush`, exactly like [`BlockDevice`].
pub trait JournalStore {
    fn read_slot(&mut self, slot: u8, buf: &mut [u8; RECORD_SIZE]) -> Result<(), IoError>;
    fn write_slot(&mut self, slot: u8, buf: &[u8; RECORD_SIZE]) -> Result<(), IoError>;
    fn flush(&mut self) -> Result<(), IoError>;
}

/// Journal slots stored in a reserved region of a block device: slot 0 at `base`, slot 1 at
/// `base + RECORD_SIZE`. The device must not be the range being moved (the mover checks this
/// when the journal is on the same disk).
#[derive(Debug)]
pub struct DeviceJournal<D> {
    device: D,
    base: u64,
}

impl<D: BlockDevice> DeviceJournal<D> {
    pub fn new(device: D, base: u64) -> Result<Self, IoError> {
        let sector = device.sector_size() as usize;
        if RECORD_SIZE % sector != 0 {
            return Err(IoError::Misaligned);
        }
        device.check_request(base, 2 * RECORD_SIZE)?;
        Ok(Self { device, base })
    }

    /// The byte range on the device the journal occupies.
    pub fn region(&self) -> core::ops::Range<u64> {
        self.base..self.base + 2 * RECORD_SIZE as u64
    }

    pub fn device(&self) -> &D {
        &self.device
    }

    pub fn device_mut(&mut self) -> &mut D {
        &mut self.device
    }

    pub fn into_device(self) -> D {
        self.device
    }

    fn slot_offset(&self, slot: u8) -> u64 {
        self.base + u64::from(slot & 1) * RECORD_SIZE as u64
    }
}

impl<D: BlockDevice> JournalStore for DeviceJournal<D> {
    fn read_slot(&mut self, slot: u8, buf: &mut [u8; RECORD_SIZE]) -> Result<(), IoError> {
        let offset = self.slot_offset(slot);
        self.device.read_at(offset, buf)
    }

    fn write_slot(&mut self, slot: u8, buf: &[u8; RECORD_SIZE]) -> Result<(), IoError> {
        let offset = self.slot_offset(slot);
        self.device.write_at(offset, buf)
    }

    fn flush(&mut self) -> Result<(), IoError> {
        self.device.flush()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoadError {
    Io(IoError),
    Decode(DecodeError),
}

impl From<IoError> for LoadError {
    fn from(e: IoError) -> Self {
        LoadError::Io(e)
    }
}

/// Read both slots and return the newest valid record, or `None` if neither slot holds one.
pub fn load_newest<J: JournalStore>(store: &mut J) -> Result<Option<MoveRecord>, LoadError> {
    let mut buf = [0u8; RECORD_SIZE];
    let mut newest: Option<MoveRecord> = None;
    for slot in 0..2u8 {
        store.read_slot(slot, &mut buf)?;
        match MoveRecord::decode(&buf) {
            Ok(record) => {
                if newest.is_none_or(|n| record.sequence > n.sequence) {
                    newest = Some(record);
                }
            }
            Err(DecodeError::Invalid) => {}
            Err(e) => return Err(LoadError::Decode(e)),
        }
    }
    Ok(newest)
}

/// Write `record` into the slot its sequence number selects, then flush.
pub fn commit<J: JournalStore>(store: &mut J, record: &MoveRecord) -> Result<(), IoError> {
    let mut buf = [0u8; RECORD_SIZE];
    record.encode(&mut buf);
    store.write_slot((record.sequence & 1) as u8, &buf)?;
    store.flush()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> MoveRecord {
        MoveRecord {
            sequence: 7,
            operation_id: [1; 16],
            disk_id: [2; 16],
            disk_size: 1 << 30,
            sector_size: 512,
            partition: PartitionKey([3; 16]),
            entry: PartitionEntry([4; ENTRY_SIZE]),
            source_offset: 1 << 20,
            target_offset: 2 << 20,
            length: 8 << 20,
            chunk_size: 1 << 20,
            direction: Direction::Backward,
            phase: Phase::Copying,
            chunks_done: 3,
            verify: true,
            source_digest: Some([5; 32]),
            map_digest: Some([6; 32]),
        }
    }

    #[test]
    fn round_trips() {
        let mut buf = [0u8; RECORD_SIZE];
        sample().encode(&mut buf);
        assert_eq!(MoveRecord::decode(&buf), Ok(sample()));
    }

    #[test]
    fn any_flipped_byte_is_rejected() {
        let mut buf = [0u8; RECORD_SIZE];
        sample().encode(&mut buf);
        for i in [0, 10, 16, 100, 2000, RECORD_SIZE - 1] {
            let mut damaged = buf;
            damaged[i] ^= 0x40;
            assert_eq!(
                MoveRecord::decode(&damaged),
                Err(DecodeError::Invalid),
                "byte {i}"
            );
        }
    }

    #[test]
    fn empty_slot_is_invalid_not_an_error() {
        assert_eq!(
            MoveRecord::decode(&[0u8; RECORD_SIZE]),
            Err(DecodeError::Invalid)
        );
    }

    #[test]
    fn newer_format_is_refused() {
        let mut buf = [0u8; RECORD_SIZE];
        sample().encode(&mut buf);
        buf[8] = 99;
        let crc = crc32(&buf[..CRC_OFFSET]);
        buf[CRC_OFFSET..].copy_from_slice(&crc.to_le_bytes());
        assert_eq!(
            MoveRecord::decode(&buf),
            Err(DecodeError::UnsupportedVersion(99))
        );
    }
}
