//! `DRIVE_LAYOUT_INFORMATION_EX` read, edited in place and written back.
//!
//! The raw buffer from `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` is kept and edited byte by byte, so
//! every field this code doesn't understand is written back exactly as Windows gave it.

use opm_core::{IoError, PartitionEntry, PartitionKey, ENTRY_SIZE};

const HEADER: usize = 48;
const ENTRY: usize = 144;

const MBR_KEY_PREFIX: &[u8; 8] = b"MBR-SLOT";

/// Start of a journaled [`PartitionEntry`], so a snapshot in another format is never written
/// into a table. The tag is followed by a u16 record count, then from byte 16 the records:
/// a u16 layout index and the raw `PARTITION_INFORMATION_EX` as Windows reported it.
const SNAPSHOT_TAG: &[u8; 8] = b"W32PIEX2";
const SNAPSHOT_RECORDS: usize = 16;
const RECORD: usize = 2 + ENTRY;
const MAX_RECORDS: usize = (ENTRY_SIZE - SNAPSHOT_RECORDS) / RECORD;

/// MBR types of an extended partition (CHS, LBA, Linux).
pub fn is_extended_type(kind: u8) -> bool {
    matches!(kind, 0x05 | 0x0F | 0x85)
}

/// One entry from a snapshot.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SnapshotRecord {
    pub index: usize,
    pub offset: u64,
    pub mbr_type: Option<u8>,
}

fn records(snapshot: &PartitionEntry) -> Result<Vec<(usize, &[u8])>, IoError> {
    let s = &snapshot.0;
    let count = usize::from(u16::from_le_bytes([s[8], s[9]]));
    if &s[..8] != SNAPSHOT_TAG || count == 0 || count > MAX_RECORDS {
        return Err(INVALID);
    }
    Ok((0..count)
        .map(|i| {
            let b = SNAPSHOT_RECORDS + i * RECORD;
            (
                usize::from(u16::from_le_bytes([s[b], s[b + 1]])),
                &s[b + 2..b + RECORD],
            )
        })
        .collect())
}

/// The entries in a snapshot, first the one the key names.
pub fn snapshot_records(snapshot: &PartitionEntry) -> Result<Vec<SnapshotRecord>, IoError> {
    Ok(records(snapshot)?
        .into_iter()
        .map(|(index, e)| SnapshotRecord {
            index,
            offset: u64_at(e, 8),
            mbr_type: (u32_at(e, 0) == 0).then(|| e[32]),
        })
        .collect())
}

/// ERROR_INVALID_DATA
const INVALID: IoError = IoError::Os(13);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LayoutStyle {
    Mbr,
    Gpt,
    Raw,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LayoutEntry {
    /// Position in the layout's entry array (for MBR, 0-3 are the primary table slots).
    pub index: usize,
    pub number: u32,
    pub offset: u64,
    pub length: u64,
    pub mbr_type: Option<u8>,
    pub gpt_type: Option<[u8; 16]>,
    pub gpt_id: Option<[u8; 16]>,
}

#[derive(Debug, Clone)]
pub struct Layout {
    raw: Vec<u8>,
    pub style: LayoutStyle,
    count: usize,
}

fn u32_at(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}

fn u64_at(b: &[u8], i: usize) -> u64 {
    let mut a = [0u8; 8];
    a.copy_from_slice(&b[i..i + 8]);
    u64::from_le_bytes(a)
}

fn bytes16(b: &[u8], i: usize) -> [u8; 16] {
    let mut a = [0u8; 16];
    a.copy_from_slice(&b[i..i + 16]);
    a
}

impl Layout {
    pub fn parse(raw: Vec<u8>) -> Result<Self, IoError> {
        if raw.len() < HEADER {
            return Err(IoError::Os(13));
        }
        let style = match u32_at(&raw, 0) {
            0 => LayoutStyle::Mbr,
            1 => LayoutStyle::Gpt,
            _ => LayoutStyle::Raw,
        };
        let count = u32_at(&raw, 4) as usize;
        if count > 1024 || HEADER + count * ENTRY > raw.len() {
            return Err(IoError::Os(13));
        }
        Ok(Self { raw, style, count })
    }

    fn entry_base(&self, index: usize) -> usize {
        HEADER + index * ENTRY
    }

    /// Non-empty entries.
    pub fn entries(&self) -> Vec<LayoutEntry> {
        (0..self.count)
            .filter_map(|index| {
                let b = self.entry_base(index);
                let e = &self.raw[b..b + ENTRY];
                let entry = LayoutEntry {
                    index,
                    number: u32_at(e, 24),
                    offset: u64_at(e, 8),
                    length: u64_at(e, 16),
                    mbr_type: (self.style == LayoutStyle::Mbr).then(|| e[32]),
                    gpt_type: (self.style == LayoutStyle::Gpt).then(|| bytes16(e, 32)),
                    gpt_id: (self.style == LayoutStyle::Gpt).then(|| bytes16(e, 48)),
                };
                let empty = entry.length == 0
                    || entry.mbr_type == Some(0)
                    || entry.gpt_type == Some([0; 16]);
                (!empty).then_some(entry)
            })
            .collect()
    }

    /// GPT disk GUID, or the MBR signature followed by `MBR` padding.
    pub fn disk_id(&self) -> [u8; 16] {
        match self.style {
            LayoutStyle::Gpt => bytes16(&self.raw, 8),
            _ => {
                let mut id = [0u8; 16];
                id[..4].copy_from_slice(&self.raw[8..12]);
                id[4..7].copy_from_slice(b"MBR");
                id
            }
        }
    }

    /// Byte range partitions may occupy.
    pub fn usable_range(&self, disk_size: u64, sector_size: u32) -> (u64, u64) {
        match self.style {
            LayoutStyle::Gpt => {
                let start = u64_at(&self.raw, 24);
                (start, start + u64_at(&self.raw, 32))
            }
            _ => (u64::from(sector_size), disk_size),
        }
    }

    /// Stable identity for the core: the GPT partition GUID, or the MBR primary slot index.
    /// `None` for MBR logical partitions, which this version does not move.
    pub fn key_of(&self, entry: &LayoutEntry) -> Option<PartitionKey> {
        match self.style {
            LayoutStyle::Gpt => entry.gpt_id.map(PartitionKey),
            LayoutStyle::Mbr if entry.index < 4 => {
                let mut key = [0u8; 16];
                key[..8].copy_from_slice(MBR_KEY_PREFIX);
                key[8] = entry.index as u8;
                Some(PartitionKey(key))
            }
            _ => None,
        }
    }

    pub fn find(&self, key: PartitionKey) -> Option<LayoutEntry> {
        self.entries()
            .into_iter()
            .find(|e| self.key_of(e) == Some(key))
    }

    /// Whether no entry overlaps `[start, start + length)`.
    pub fn range_is_free(&self, start: u64, length: u64) -> bool {
        let end = start.saturating_add(length);
        self.entries()
            .iter()
            .all(|e| e.offset.saturating_add(e.length) <= start || end <= e.offset)
    }

    /// MBR disk signature.
    pub fn signature(&self) -> [u8; 4] {
        [self.raw[8], self.raw[9], self.raw[10], self.raw[11]]
    }

    /// An MBR extended partition: it moves as one block with the logical drives inside it.
    pub fn is_container(&self, entry: &LayoutEntry) -> bool {
        entry.index < 4 && entry.mbr_type.is_some_and(is_extended_type)
    }

    /// The entries that belong to `entry`'s move: itself and, for an extended partition, every
    /// logical drive and chain link inside it.
    fn members(&self, entry: &LayoutEntry) -> Vec<usize> {
        let mut members = vec![entry.index];
        if self.is_container(entry) {
            members.extend(
                self.entries()
                    .iter()
                    .filter(|e| e.index >= 4)
                    .map(|e| e.index),
            );
        }
        members
    }

    /// The entries exactly as Windows reported them, for [`Self::insert`].
    pub fn snapshot(&self, entry: &LayoutEntry) -> Result<PartitionEntry, IoError> {
        let members = self.members(entry);
        if members.len() > MAX_RECORDS {
            return Err(IoError::Os(8)); // ERROR_NOT_ENOUGH_MEMORY: too many logical drives
        }
        let mut out = [0u8; ENTRY_SIZE];
        out[..8].copy_from_slice(SNAPSHOT_TAG);
        out[8..10].copy_from_slice(&(members.len() as u16).to_le_bytes());
        for (i, index) in members.into_iter().enumerate() {
            let b = SNAPSHOT_RECORDS + i * RECORD;
            let e = self.entry_base(index);
            out[b..b + 2].copy_from_slice(&(index as u16).to_le_bytes());
            out[b + 2..b + RECORD].copy_from_slice(&self.raw[e..e + ENTRY]);
        }
        Ok(PartitionEntry(out))
    }

    /// Drop an entry. GPT entries leave the array; an MBR primary slot is emptied in place so
    /// the other slots keep their positions, and an extended partition takes its logical
    /// drives with it.
    pub fn remove(&mut self, entry: &LayoutEntry) {
        let b = self.entry_base(entry.index);
        match self.style {
            LayoutStyle::Mbr => {
                if self.is_container(entry) {
                    self.set_count(4);
                }
                self.raw[b..b + ENTRY].fill(0);
            }
            _ => {
                self.raw.drain(b..b + ENTRY);
                self.set_count(self.count - 1);
            }
        }
    }

    fn set_count(&mut self, count: usize) {
        self.raw.resize(HEADER + count * ENTRY, 0);
        self.count = count;
        self.raw[4..8].copy_from_slice(&(count as u32).to_le_bytes());
    }

    /// Put back entries removed by [`Self::remove`], shifted so the first starts at `offset`.
    /// `key` must be the key the first had.
    pub fn insert(
        &mut self,
        key: PartitionKey,
        snapshot: &PartitionEntry,
        offset: u64,
        sector_size: u32,
    ) -> Result<(), IoError> {
        let records = records(snapshot)?;
        let (_, first) = records[0];
        if records
            .iter()
            .any(|(_, e)| u32_at(e, 0) != u32_at(&self.raw, 0))
        {
            return Err(INVALID);
        }
        let shift = |old: u64| (old + offset).checked_sub(u64_at(first, 8)).ok_or(INVALID);
        match self.style {
            LayoutStyle::Gpt => {
                if records.len() != 1 || bytes16(first, 48) != key.0 {
                    return Err(INVALID);
                }
                let index = self.count;
                self.set_count(index + 1);
                let b = self.entry_base(index);
                self.raw[b..b + ENTRY].copy_from_slice(first);
                self.set_offset(index, offset, sector_size);
            }
            LayoutStyle::Mbr => {
                let slot = usize::from(key.0[8]);
                if key.0[..8] != *MBR_KEY_PREFIX || slot > 3 || records[0].0 != slot {
                    return Err(INVALID);
                }
                if self.raw[self.entry_base(slot) + 32] != 0 {
                    return Err(IoError::Os(183)); // ERROR_ALREADY_EXISTS: the slot was reused
                }
                let logicals = &records[1..];
                if !logicals.is_empty() {
                    if self.entries().iter().any(|e| e.index >= 4) {
                        return Err(IoError::Os(183)); // another extended partition exists
                    }
                    let last = logicals.iter().map(|(i, _)| *i).max().unwrap_or(3);
                    self.set_count((last / 4 + 1) * 4);
                }
                for (index, e) in &records {
                    let b = self.entry_base(*index);
                    self.raw[b..b + ENTRY].copy_from_slice(e);
                    // Logical drives and chain links keep HiddenSectors: it is relative to
                    // their EBR, and the whole container shifts by the same amount.
                    let moved = shift(u64_at(e, 8))?;
                    self.raw[b + 8..b + 16].copy_from_slice(&moved.to_le_bytes());
                }
                self.set_offset(slot, offset, sector_size);
            }
            LayoutStyle::Raw => return Err(INVALID),
        }
        Ok(())
    }

    /// Grow an MBR extended partition into free space after it. Only the container's own entry
    /// changes: its logical drives and their chain are relative to its start, which stays put.
    pub fn grow_container(&mut self, entry: &LayoutEntry, new_length: u64) -> Result<(), IoError> {
        if !self.is_container(entry) || new_length <= entry.length {
            return Err(INVALID);
        }
        let added_start = entry.offset + entry.length;
        if !self.range_is_free(added_start, new_length - entry.length) {
            return Err(IoError::Os(183)); // something else is in the way
        }
        let b = self.entry_base(entry.index);
        self.raw[b + 16..b + 24].copy_from_slice(&new_length.to_le_bytes());
        Ok(())
    }

    pub fn set_offset(&mut self, index: usize, offset: u64, sector_size: u32) {
        let b = self.entry_base(index);
        self.raw[b + 8..b + 16].copy_from_slice(&offset.to_le_bytes());
        if self.style == LayoutStyle::Mbr {
            // PARTITION_INFORMATION_MBR.HiddenSectors: the start in sectors.
            let hidden = (offset / u64::from(sector_size)) as u32;
            self.raw[b + 36..b + 40].copy_from_slice(&hidden.to_le_bytes());
        }
    }

    /// Buffer for `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`, with every entry marked for rewrite.
    pub fn to_set_buffer(&self) -> Vec<u8> {
        let mut out = self.raw[..HEADER + self.count * ENTRY].to_vec();
        for index in 0..self.count {
            out[HEADER + index * ENTRY + 28] = 1; // RewritePartition
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn gpt(entries: &[(u64, u64, [u8; 16], [u8; 16])]) -> Layout {
        let mut raw = vec![0u8; HEADER + entries.len() * ENTRY];
        raw[0..4].copy_from_slice(&1u32.to_le_bytes());
        raw[4..8].copy_from_slice(&(entries.len() as u32).to_le_bytes());
        raw[8..24].copy_from_slice(&[9; 16]);
        raw[24..32].copy_from_slice(&17408u64.to_le_bytes());
        raw[32..40].copy_from_slice(&(1u64 << 30).to_le_bytes());
        for (i, (offset, length, kind, id)) in entries.iter().enumerate() {
            let b = HEADER + i * ENTRY;
            raw[b..b + 4].copy_from_slice(&1u32.to_le_bytes());
            raw[b + 8..b + 16].copy_from_slice(&offset.to_le_bytes());
            raw[b + 16..b + 24].copy_from_slice(&length.to_le_bytes());
            raw[b + 24..b + 28].copy_from_slice(&((i + 1) as u32).to_le_bytes());
            raw[b + 32..b + 48].copy_from_slice(kind);
            raw[b + 48..b + 64].copy_from_slice(id);
        }
        Layout::parse(raw).unwrap()
    }

    #[test]
    fn gpt_entry_survives_remove_and_insert_at_a_new_offset() {
        let mut layout = gpt(&[
            (1 << 20, 1 << 24, [1; 16], [2; 16]),
            (64 << 20, 1 << 24, [1; 16], [3; 16]),
        ]);
        let key = PartitionKey([2; 16]);
        let entry = layout.find(key).unwrap();
        // Attributes and name must come back too.
        let b = HEADER + 64;
        layout.raw[b..b + 8].copy_from_slice(&0x8000_0000_0000_0001u64.to_le_bytes());
        layout.raw[b + 8] = b'X';
        let snapshot = layout.snapshot(&entry).unwrap();

        layout.remove(&entry);
        let removed = Layout::parse(layout.to_set_buffer()).unwrap();
        assert!(removed.find(key).is_none());
        assert_eq!(removed.entries().len(), 1);
        assert!(removed.range_is_free(1 << 20, 1 << 24));
        assert!(!removed.range_is_free(70 << 20, 1 << 20));

        layout.insert(key, &snapshot, 5 << 20, 512).unwrap();
        let back = Layout::parse(layout.to_set_buffer()).unwrap();
        let moved = back.find(key).unwrap();
        assert_eq!((moved.offset, moved.length), (5 << 20, 1 << 24));
        let mb = HEADER + moved.index * ENTRY;
        let rec = SNAPSHOT_RECORDS + 2;
        assert_eq!(
            back.raw[mb + 32..mb + ENTRY],
            snapshot.0[rec + 32..rec + ENTRY]
        );
        assert_eq!(back.raw[mb + 28], 1);
        assert_eq!(back.usable_range(0, 512), (17408, 17408 + (1 << 30)));
        assert_eq!(back.disk_id(), [9; 16]);
    }

    #[test]
    fn snapshot_with_the_wrong_key_or_tag_is_refused() {
        let mut layout = gpt(&[(1 << 20, 1 << 24, [1; 16], [2; 16])]);
        let entry = layout.find(PartitionKey([2; 16])).unwrap();
        let snapshot = layout.snapshot(&entry).unwrap();
        layout.remove(&entry);
        assert_eq!(
            layout.insert(PartitionKey([7; 16]), &snapshot, 5 << 20, 512),
            Err(INVALID)
        );
        let mut bad = snapshot;
        bad.0[0] ^= 1;
        assert_eq!(
            layout.insert(PartitionKey([2; 16]), &bad, 5 << 20, 512),
            Err(INVALID)
        );
    }

    #[test]
    fn mbr_slot_is_emptied_and_refilled_in_place() {
        let mut raw = vec![0u8; HEADER + 4 * ENTRY];
        raw[4..8].copy_from_slice(&4u32.to_le_bytes());
        for (i, (offset, kind, boot)) in [(1u64 << 20, 0x07u8, 1u8), (64 << 20, 0x07, 0)]
            .iter()
            .enumerate()
        {
            let b = HEADER + i * ENTRY;
            raw[b + 8..b + 16].copy_from_slice(&offset.to_le_bytes());
            raw[b + 16..b + 24].copy_from_slice(&(16u64 << 20).to_le_bytes());
            raw[b + 32] = *kind;
            raw[b + 33] = *boot;
        }
        let mut layout = Layout::parse(raw).unwrap();
        let first = layout.entries()[0];
        let key = layout.key_of(&first).unwrap();
        let snapshot = layout.snapshot(&first).unwrap();
        layout.remove(&first);
        assert_eq!(layout.entries().len(), 1);
        assert_eq!(
            layout.entries()[0].index,
            1,
            "other slots keep their position"
        );
        layout.insert(key, &snapshot, 32 << 20, 512).unwrap();
        let back = layout.find(key).unwrap();
        assert_eq!(
            (back.index, back.offset, back.mbr_type),
            (0, 32 << 20, Some(0x07))
        );
        assert_eq!(layout.raw[HEADER + 33], 1, "active flag restored");
        assert_eq!(
            u32_at(&layout.raw, HEADER + 36),
            (32 << 20) / 512,
            "hidden sectors follow the start"
        );
        assert_eq!(
            layout.insert(key, &snapshot, 32 << 20, 512),
            Err(IoError::Os(183))
        );
    }

    /// Primary at 1 MiB, extended at 64 MiB holding two logical drives, as Windows reports
    /// it: groups of four entries per EBR, each a logical drive then the link to the next EBR.
    fn mbr_with_logicals() -> Layout {
        const MIB: u64 = 1 << 20;
        let mut raw = vec![0u8; HEADER + 12 * ENTRY];
        raw[4..8].copy_from_slice(&12u32.to_le_bytes());
        raw[8..12].copy_from_slice(&0x1234_5678u32.to_le_bytes());
        let mut put = |i: usize, offset: u64, length: u64, kind: u8, hidden: u32| {
            let b = HEADER + i * ENTRY;
            raw[b + 8..b + 16].copy_from_slice(&offset.to_le_bytes());
            raw[b + 16..b + 24].copy_from_slice(&length.to_le_bytes());
            raw[b + 32] = kind;
            raw[b + 36..b + 40].copy_from_slice(&hidden.to_le_bytes());
        };
        put(0, MIB, 32 * MIB, 0x07, 2048);
        put(1, 64 * MIB, 256 * MIB, 0x05, 131_072);
        put(4, 64 * MIB + 65536, 64 * MIB, 0x07, 128);
        put(5, 128 * MIB + 65536, 64 * MIB + 65536, 0x05, 131_200);
        put(8, 128 * MIB + 131_072, 64 * MIB, 0x07, 128);
        Layout::parse(raw).unwrap()
    }

    #[test]
    fn extended_partition_moves_with_its_logical_drives() {
        const MIB: u64 = 1 << 20;
        let mut layout = mbr_with_logicals();
        let extended = layout.entries().into_iter().find(|e| e.index == 1).unwrap();
        assert!(layout.is_container(&extended));
        let key = layout.key_of(&extended).unwrap();
        let snapshot = layout.snapshot(&extended).unwrap();
        let before: Vec<_> = layout.entries();
        assert_eq!(snapshot_records(&snapshot).unwrap().len(), 4);

        layout.remove(&extended);
        let removed = Layout::parse(layout.to_set_buffer()).unwrap();
        assert_eq!(removed.entries().len(), 1, "only the primary is left");
        assert!(removed.range_is_free(64 * MIB, 256 * MIB));

        layout.insert(key, &snapshot, 512 * MIB, 512).unwrap();
        let back = Layout::parse(layout.to_set_buffer()).unwrap();
        let after = back.entries();
        assert_eq!(after.len(), before.len());
        let shift = 448 * MIB;
        for (b, a) in before.iter().zip(&after) {
            assert_eq!(a.index, b.index);
            assert_eq!(a.length, b.length);
            assert_eq!(a.mbr_type, b.mbr_type);
            let expected = if b.index == 0 {
                b.offset
            } else {
                b.offset + shift
            };
            assert_eq!(a.offset, expected, "entry {}", b.index);
        }
        let hidden = |i: usize| u32_at(&back.raw, HEADER + i * ENTRY + 36);
        assert_eq!(
            hidden(1),
            (512 * MIB / 512) as u32,
            "the extended entry's start is absolute"
        );
        assert_eq!(
            (hidden(4), hidden(5), hidden(8)),
            (128, 131_200, 128),
            "logical ones are relative"
        );
    }

    #[test]
    fn extended_partition_grows_into_free_space_only() {
        const MIB: u64 = 1 << 20;
        let mut layout = mbr_with_logicals();
        let extended = layout.entries().into_iter().find(|e| e.index == 1).unwrap();
        let before: Vec<_> = layout
            .entries()
            .into_iter()
            .filter(|e| e.index != 1)
            .collect();
        layout.grow_container(&extended, 512 * MIB).unwrap();
        let back = Layout::parse(layout.to_set_buffer()).unwrap();
        let grown = back.entries().into_iter().find(|e| e.index == 1).unwrap();
        assert_eq!((grown.offset, grown.length), (64 * MIB, 512 * MIB));
        let after: Vec<_> = back
            .entries()
            .into_iter()
            .filter(|e| e.index != 1)
            .collect();
        assert_eq!(before, after, "nothing else changes");
        assert_eq!(
            layout.grow_container(&grown, 256 * MIB),
            Err(INVALID),
            "never shrinks"
        );
        let primary = back.entries()[0];
        assert_eq!(
            layout.grow_container(&primary, 64 * MIB),
            Err(INVALID),
            "only containers"
        );
    }

    #[test]
    fn extended_partition_cannot_grow_over_another() {
        const MIB: u64 = 1 << 20;
        let mut layout = mbr_with_logicals();
        let b = HEADER + 2 * ENTRY;
        layout.raw[b + 8..b + 16].copy_from_slice(&(400 * MIB).to_le_bytes());
        layout.raw[b + 16..b + 24].copy_from_slice(&(MIB).to_le_bytes());
        layout.raw[b + 32] = 0x07;
        let extended = layout.entries().into_iter().find(|e| e.index == 1).unwrap();
        assert_eq!(
            layout.grow_container(&extended, 512 * MIB),
            Err(IoError::Os(183))
        );
    }

    #[test]
    fn extended_partition_cannot_come_back_over_another() {
        let mut layout = mbr_with_logicals();
        let extended = layout.entries().into_iter().find(|e| e.index == 1).unwrap();
        let key = layout.key_of(&extended).unwrap();
        let snapshot = layout.snapshot(&extended).unwrap();
        // Not removed: its logical drives are still in the table.
        layout.raw[HEADER + ENTRY + 32] = 0;
        assert_eq!(
            layout.insert(key, &snapshot, 512 << 20, 512),
            Err(IoError::Os(183))
        );
    }
}
