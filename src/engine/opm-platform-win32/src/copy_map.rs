//! The copy map: which parts of a partition hold data and must be moved. Built from the NTFS
//! allocation bitmaps of the locked volumes (free clusters are left out), saved next to the
//! journal before the move starts, and identified in the journal by its SHA-256, so recovery
//! after a crash copies exactly the same parts.

use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};

use opm_core::{CopyMap, IoError};
use sha2::{Digest, Sha256};

const MAGIC: &[u8; 8] = b"OPMMAP01";

/// Where the map of the move journaled at `journal` is kept.
pub fn path_for(journal: &Path) -> PathBuf {
    let mut name = journal.as_os_str().to_owned();
    name.push(".map");
    PathBuf::from(name)
}

/// The map of a partition at `offset` (bytes) of `length` bytes on a disk with `sector`-byte
/// sectors, leaving out `free` (absolute disk byte ranges known to hold no data). Only whole
/// sectors inside the partition are left out.
pub fn build(offset: u64, length: u64, sector: u64, free: &[(u64, u64)]) -> CopyMap {
    let mut holes: Vec<(u64, u64)> = free
        .iter()
        .filter_map(|&(start, len)| {
            let a = start.max(offset).next_multiple_of(sector);
            let b = (start + len).min(offset + length) / sector * sector;
            (a < b).then(|| (a - offset, b - offset))
        })
        .collect();
    holes.sort_unstable();
    let mut ranges = Vec::new();
    let mut at = 0;
    for (a, b) in holes {
        if a > at {
            ranges.push((at, a - at));
        }
        at = at.max(b);
    }
    if at < length {
        ranges.push((at, length - at));
    }
    let mut map = CopyMap {
        ranges,
        digest: None,
    };
    map.digest = Some(digest(&encode(&map)));
    map
}

fn encode(map: &CopyMap) -> Vec<u8> {
    let mut out = Vec::with_capacity(16 + map.ranges.len() * 16);
    out.extend_from_slice(MAGIC);
    out.extend_from_slice(&(map.ranges.len() as u64).to_le_bytes());
    for (start, len) in &map.ranges {
        out.extend_from_slice(&start.to_le_bytes());
        out.extend_from_slice(&len.to_le_bytes());
    }
    out
}

fn digest(bytes: &[u8]) -> [u8; 32] {
    Sha256::digest(bytes).into()
}

/// Write the map durably (flushed) before the journal refers to it.
pub fn save(path: &Path, map: &CopyMap) -> Result<(), IoError> {
    let io = |e: std::io::Error| IoError::Os(e.raw_os_error().unwrap_or(-1));
    let mut file = File::create(path).map_err(io)?;
    file.write_all(&encode(map)).map_err(io)?;
    file.sync_all().map_err(io)
}

/// Read a saved map. Its digest is recomputed from the file, so a damaged or different map
/// can never match the journal.
pub fn load(path: &Path) -> Result<CopyMap, IoError> {
    let bytes = std::fs::read(path).map_err(|e| IoError::Os(e.raw_os_error().unwrap_or(-1)))?;
    let corrupt = IoError::Os(13); // ERROR_INVALID_DATA
    if bytes.len() < 16 || &bytes[..8] != MAGIC {
        return Err(corrupt);
    }
    let count = u64::from_le_bytes(bytes[8..16].try_into().map_err(|_| corrupt)?) as usize;
    if bytes.len() != 16 + count * 16 {
        return Err(corrupt);
    }
    let ranges = bytes[16..]
        .chunks_exact(16)
        .map(|c| {
            (
                u64::from_le_bytes(c[..8].try_into().unwrap_or_default()),
                u64::from_le_bytes(c[8..].try_into().unwrap_or_default()),
            )
        })
        .collect();
    Ok(CopyMap {
        ranges,
        digest: Some(digest(&bytes)),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const MIB: u64 = 1 << 20;

    #[test]
    fn free_space_is_left_out_and_the_rest_kept() {
        // Partition at 10 MiB, 8 MiB long; free runs partly outside it and unaligned.
        let map = build(
            10 * MIB,
            8 * MIB,
            512,
            &[(9 * MIB, 2 * MIB), (13 * MIB + 100, MIB), (20 * MIB, MIB)],
        );
        // Left out: [0, 1 MiB) and [3 MiB + 512, 4 MiB), the unaligned run rounded inwards.
        assert_eq!(map.ranges, vec![(MIB, 2 * MIB + 512), (4 * MIB, 4 * MIB)]);
        assert_eq!(map.bytes(), 6 * MIB + 512);
    }

    #[test]
    fn no_free_space_means_the_whole_partition() {
        assert_eq!(build(MIB, 4 * MIB, 512, &[]).ranges, vec![(0, 4 * MIB)]);
    }

    #[test]
    fn saved_map_loads_with_the_same_digest() {
        let path = std::env::temp_dir().join(format!("opm-map-test-{}.map", std::process::id()));
        let map = build(MIB, 64 * MIB, 4096, &[(5 * MIB, 3 * MIB), (40 * MIB, MIB)]);
        save(&path, &map).unwrap();
        assert_eq!(load(&path).unwrap(), map);
        let mut bytes = std::fs::read(&path).unwrap();
        bytes[20] ^= 1;
        std::fs::write(&path, &bytes).unwrap();
        assert_ne!(load(&path).unwrap().digest, map.digest);
        std::fs::remove_file(&path).unwrap();
    }
}
