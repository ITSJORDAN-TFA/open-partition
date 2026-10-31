//! SHA-256 fingerprints of the data being moved, spread over every CPU core.
//!
//! Large inputs are cut into fixed 8 MiB segments (each part on its own: the mover always
//! fingerprints the source and the target with the same parts), the segments are hashed in
//! parallel, and the fingerprint is the SHA-256 of a domain tag and the segment hashes in
//! order. Small inputs are hashed directly. The result only has to be identical for identical
//! input, which this guarantees for any number of cores.

use std::thread;

use sha2::{Digest, Sha256};

const SEGMENT: usize = 8 << 20;
const TREE_TAG: &[u8] = b"OPM-SHA256-TREE-8MiB";

pub fn fingerprint(parts: &[&[u8]]) -> [u8; 32] {
    fingerprint_with(parts, available_cores())
}

fn available_cores() -> usize {
    thread::available_parallelism().map_or(1, |n| n.get())
}

fn fingerprint_with(parts: &[&[u8]], cores: usize) -> [u8; 32] {
    let total: usize = parts.iter().map(|p| p.len()).sum();
    if total <= 2 * SEGMENT {
        let mut hasher = Sha256::new();
        for part in parts {
            hasher.update(part);
        }
        return hasher.finalize().into();
    }
    let segments: Vec<&[u8]> = parts.iter().flat_map(|p| p.chunks(SEGMENT)).collect();
    let mut hashes = vec![[0u8; 32]; segments.len()];
    let per_core = segments.len().div_ceil(cores.max(1));
    thread::scope(|scope| {
        for (inputs, outputs) in segments.chunks(per_core).zip(hashes.chunks_mut(per_core)) {
            scope.spawn(move || {
                for (input, output) in inputs.iter().zip(outputs.iter_mut()) {
                    *output = Sha256::digest(input).into();
                }
            });
        }
    });
    let mut hasher = Sha256::new();
    hasher.update(TREE_TAG);
    for hash in &hashes {
        hasher.update(hash);
    }
    hasher.finalize().into()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn data(len: usize, seed: u8) -> Vec<u8> {
        (0..len)
            .map(|i| (i as u8).wrapping_mul(31) ^ seed ^ (i >> 13) as u8)
            .collect()
    }

    #[test]
    fn same_for_any_number_of_cores() {
        let a = data(70 << 20, 1);
        let b = data(3 << 20, 2);
        let parts: [&[u8]; 2] = [&a, &b];
        let one = fingerprint_with(&parts, 1);
        for cores in [2, 3, 4, 16, 64] {
            assert_eq!(fingerprint_with(&parts, cores), one, "{cores} cores");
        }
    }

    #[test]
    fn any_changed_byte_changes_it() {
        let mut a = data(40 << 20, 3);
        let before = fingerprint(&[&a]);
        for at in [0, SEGMENT - 1, SEGMENT, (40 << 20) - 1] {
            a[at] ^= 1;
            assert_ne!(fingerprint(&[&a]), before, "byte {at}");
            a[at] ^= 1;
        }
        assert_eq!(fingerprint(&[&a]), before);
    }

    #[test]
    fn small_input_is_plain_sha256() {
        let a = data(1 << 20, 4);
        assert_eq!(
            fingerprint(&[&a[..100], &a[100..]]),
            <[u8; 32]>::from(Sha256::digest(&a))
        );
    }
}
