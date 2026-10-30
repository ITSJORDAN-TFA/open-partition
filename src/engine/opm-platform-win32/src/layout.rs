//! `DRIVE_LAYOUT_INFORMATION_EX` read, edited in place and written back.
//!
//! The raw buffer from `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` is kept and edited byte by byte, so
//! every field this code doesn't understand is written back exactly as Windows gave it.

use opm_core::{IoError, PartitionEntry, PartitionKey, ENTRY_SIZE};

const HEADER: usize = 48;
const ENTRY: usize = 144;

const MBR_KEY_PREFIX: &[u8; 8] = b"MBR-SLOT";

/// Tag after the raw `PARTITION_INFORMATION_EX` in a journaled [`PartitionEntry`], so a
/// snapshot from another format is never written into a table.
const SNAPSHOT_TAG: &[u8; 8] = b"W32PIEX1";

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

    /// The entry exactly as Windows reported it, for [`Self::insert`].
    pub fn snapshot(&self, entry: &LayoutEntry) -> PartitionEntry {
        let b = self.entry_base(entry.index);
        let mut out = [0u8; ENTRY_SIZE];
        out[..ENTRY].copy_from_slice(&self.raw[b..b + ENTRY]);
        out[ENTRY..ENTRY + 8].copy_from_slice(SNAPSHOT_TAG);
        PartitionEntry(out)
    }

    /// Drop an entry. GPT entries leave the array; an MBR primary slot is emptied in place so
    /// the other slots keep their positions.
    pub fn remove(&mut self, index: usize) {
        let b = self.entry_base(index);
        match self.style {
            LayoutStyle::Mbr => self.raw[b..b + ENTRY].fill(0),
            _ => {
                self.raw.drain(b..b + ENTRY);
                self.count -= 1;
                self.raw[4..8].copy_from_slice(&(self.count as u32).to_le_bytes());
            }
        }
    }

    /// Put back an entry removed by [`Self::remove`], starting at `offset`. `key` must be the
    /// key it had.
    pub fn insert(
        &mut self,
        key: PartitionKey,
        snapshot: &PartitionEntry,
        offset: u64,
        sector_size: u32,
    ) -> Result<(), IoError> {
        if &snapshot.0[ENTRY..ENTRY + 8] != SNAPSHOT_TAG
            || u32_at(&snapshot.0, 0) != u32_at(&self.raw, 0)
        {
            return Err(INVALID);
        }
        let entry = &snapshot.0[..ENTRY];
        let index = match self.style {
            LayoutStyle::Gpt => {
                if bytes16(entry, 48) != key.0 {
                    return Err(INVALID);
                }
                self.raw.truncate(HEADER + self.count * ENTRY);
                self.raw.extend_from_slice(entry);
                self.count += 1;
                self.raw[4..8].copy_from_slice(&(self.count as u32).to_le_bytes());
                self.count - 1
            }
            LayoutStyle::Mbr => {
                if key.0[..8] != *MBR_KEY_PREFIX || key.0[8] > 3 {
                    return Err(INVALID);
                }
                let index = usize::from(key.0[8]);
                let b = self.entry_base(index);
                if self.raw[b + 32] != 0 {
                    return Err(IoError::Os(183)); // ERROR_ALREADY_EXISTS: the slot was reused
                }
                self.raw[b..b + ENTRY].copy_from_slice(entry);
                index
            }
            LayoutStyle::Raw => return Err(INVALID),
        };
        self.set_offset(index, offset, sector_size);
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
        let snapshot = layout.snapshot(&entry);

        layout.remove(entry.index);
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
        assert_eq!(back.raw[mb + 32..mb + ENTRY], snapshot.0[32..ENTRY]);
        assert_eq!(back.raw[mb + 28], 1);
        assert_eq!(back.usable_range(0, 512), (17408, 17408 + (1 << 30)));
        assert_eq!(back.disk_id(), [9; 16]);
    }

    #[test]
    fn snapshot_with_the_wrong_key_or_tag_is_refused() {
        let mut layout = gpt(&[(1 << 20, 1 << 24, [1; 16], [2; 16])]);
        let entry = layout.find(PartitionKey([2; 16])).unwrap();
        let snapshot = layout.snapshot(&entry);
        layout.remove(entry.index);
        assert_eq!(
            layout.insert(PartitionKey([7; 16]), &snapshot, 5 << 20, 512),
            Err(INVALID)
        );
        let mut bad = snapshot;
        bad.0[ENTRY] ^= 1;
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
        let snapshot = layout.snapshot(&first);
        layout.remove(first.index);
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
}
