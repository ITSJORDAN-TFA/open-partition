//! A narrow, read-only NTFS parser that finds the cluster allocation bitmap (`$Bitmap`) with
//! raw reads: boot sector -> `$MFT` record 0 -> record 6 -> its unnamed `$DATA` runlist -> the
//! bitmap bytes. Used on a locked, dismounted volume, where `FSCTL_GET_VOLUME_BITMAP` isn't
//! available (Server 2016 answers ERROR_NOT_READY under the lock) and nothing can allocate.
//!
//! Everything is validated and anything unexpected is an error: the caller then copies the whole
//! volume. A wrong answer here would leave data behind, so the parser never guesses.

use std::fmt;

use opm_core::IoError;

/// NTFS protects multi-sector structures in 512-byte strides, whatever the sector size.
const FIXUP_STRIDE: usize = 512;
const RECORD_MFT: u64 = 0;
const RECORD_VOLUME: u64 = 3;
const RECORD_BITMAP: u64 = 6;
const ATTR_ATTRIBUTE_LIST: u32 = 0x20;
const ATTR_VOLUME_INFORMATION: u32 = 0x70;
const ATTR_DATA: u32 = 0x80;
const ATTR_END: u32 = 0xFFFF_FFFF;
/// Compressed, encrypted or sparse data: none of these belong on `$MFT` or `$Bitmap`.
const ATTR_FLAGS_UNSUPPORTED: u16 = 0x00FF | 0x4000 | 0x8000;
const VOLUME_DIRTY: u16 = 0x0001;
/// No real volume needs more runs or a larger bitmap than this (1 GiB of bitmap is 32 TiB of
/// 4 KiB clusters); past it, something is wrong.
const MAX_RUNS: usize = 1 << 16;
const MAX_BITMAP_BYTES: u64 = 1 << 30;

/// The allocation bitmap of an NTFS volume: bit `n` of `bits` set means cluster `n` is in use.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Bitmap {
    pub cluster: u64,
    pub clusters: u64,
    pub bits: Vec<u8>,
}

impl Bitmap {
    pub fn is_used(&self, lcn: u64) -> bool {
        self.bits
            .get((lcn / 8) as usize)
            .is_none_or(|b| b >> (lcn % 8) & 1 == 1)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NtfsError {
    Read(IoError),
    Invalid(&'static str),
    /// NTFS found corruption and wants chkdsk: no bitmap, from any source, is trustworthy.
    Dirty,
}

impl fmt::Display for NtfsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Read(e) => write!(f, "raw read failed ({e})"),
            Self::Invalid(why) => write!(f, "unexpected NTFS layout: {why}"),
            Self::Dirty => f.write_str("volume is marked dirty (run chkdsk /f)"),
        }
    }
}

type Result<T> = std::result::Result<T, NtfsError>;

fn invalid<T>(why: &'static str) -> Result<T> {
    Err(NtfsError::Invalid(why))
}

/// Reads `buf.len()` bytes at a byte offset from the start of the volume.
pub type Reader<'a> = dyn FnMut(u64, &mut [u8]) -> std::result::Result<(), IoError> + 'a;

fn get<const N: usize>(b: &[u8], at: usize) -> Result<[u8; N]> {
    b.get(
        at..at
            .checked_add(N)
            .ok_or(NtfsError::Invalid("offset overflow"))?,
    )
    .and_then(|s| s.try_into().ok())
    .ok_or(NtfsError::Invalid("field outside its structure"))
}

fn u8_at(b: &[u8], at: usize) -> Result<u8> {
    Ok(get::<1>(b, at)?[0])
}

fn u16_at(b: &[u8], at: usize) -> Result<u16> {
    Ok(u16::from_le_bytes(get(b, at)?))
}

fn u32_at(b: &[u8], at: usize) -> Result<u32> {
    Ok(u32::from_le_bytes(get(b, at)?))
}

fn u64_at(b: &[u8], at: usize) -> Result<u64> {
    Ok(u64::from_le_bytes(get(b, at)?))
}

/// What the boot sector says about the volume.
#[derive(Debug, Clone, Copy)]
struct Geometry {
    cluster: u64,
    clusters: u64,
    mft_lcn: u64,
    record: usize,
}

fn parse_boot(boot: &[u8], volume_len: u64) -> Result<Geometry> {
    if boot.get(3..11) != Some(b"NTFS    ") || u16_at(boot, 510)? != 0xAA55 {
        return invalid("no NTFS boot sector");
    }
    let sector = u64::from(u16_at(boot, 11)?);
    if !sector.is_power_of_two() || !(512..=4096).contains(&sector) {
        return invalid("bytes per sector");
    }
    let spc = u8_at(boot, 13)?;
    let cluster = match spc {
        1..=0x80 if spc.is_power_of_two() => sector * u64::from(spc),
        // Clusters above 64 KiB: the field holds -log2(sectors per cluster).
        0xF4..=0xFF => sector << (256 - u32::from(spc)),
        _ => return invalid("sectors per cluster"),
    };
    if cluster > 2 << 20 {
        return invalid("cluster size");
    }
    let total_sectors = u64_at(boot, 40)?;
    if total_sectors
        .checked_mul(sector)
        .is_none_or(|bytes| bytes > volume_len)
    {
        return invalid("file system larger than the volume");
    }
    let clusters = total_sectors / (cluster / sector);
    if clusters == 0 {
        return invalid("no clusters");
    }
    let record = match u8_at(boot, 64)? as i8 {
        n @ 1..=127 => cluster.checked_mul(n as u64).unwrap_or(0),
        n @ -31..=-1 => 1u64 << -n,
        _ => 0,
    };
    if !record.is_power_of_two() || !(1024..=4096).contains(&record) {
        return invalid("MFT record size");
    }
    let mft_lcn = u64_at(boot, 48)?;
    if mft_lcn == 0 || mft_lcn >= clusters {
        return invalid("MFT location");
    }
    Ok(Geometry {
        cluster,
        clusters,
        mft_lcn,
        record: record as usize,
    })
}

/// One extent of a non-resident stream: `len` clusters at `lcn`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Run {
    lcn: u64,
    len: u64,
}

/// A non-resident attribute's sizes and runs, all checked against each other and the volume.
#[derive(Debug, Clone)]
struct Stream {
    runs: Vec<Run>,
    data_size: u64,
    initialized: u64,
}

fn signed_le(bytes: &[u8]) -> i64 {
    let mut v = 0i64;
    for (i, &b) in bytes.iter().enumerate() {
        v |= i64::from(b) << (8 * i);
    }
    let bits = 8 * bytes.len() as u32;
    if bits < 64 {
        // Sign-extend from the top bit of the last byte.
        v = (v << (64 - bits)) >> (64 - bits);
    }
    v
}

fn parse_runlist(list: &[u8], geo: &Geometry) -> Result<Vec<Run>> {
    let mut runs = Vec::new();
    let mut at = 0usize;
    let mut lcn = 0i64;
    loop {
        let header = *list
            .get(at)
            .ok_or(NtfsError::Invalid("runlist not terminated"))?;
        if header == 0 {
            return Ok(runs);
        }
        let (len_size, off_size) = (usize::from(header & 0x0F), usize::from(header >> 4));
        if !(1..=8).contains(&len_size) || off_size > 8 {
            return invalid("runlist entry size");
        }
        if off_size == 0 {
            return invalid("sparse run");
        }
        let len_bytes = list
            .get(at + 1..at + 1 + len_size)
            .ok_or(NtfsError::Invalid("runlist truncated"))?;
        let off_bytes = list
            .get(at + 1 + len_size..at + 1 + len_size + off_size)
            .ok_or(NtfsError::Invalid("runlist truncated"))?;
        let len = signed_le(len_bytes);
        if len <= 0 {
            return invalid("run length");
        }
        lcn = lcn
            .checked_add(signed_le(off_bytes))
            .ok_or(NtfsError::Invalid("run offset"))?;
        let (start, len) = (lcn as u64, len as u64);
        if lcn <= 0 || start.checked_add(len).is_none_or(|end| end > geo.clusters) {
            return invalid("run outside the volume");
        }
        runs.push(Run { lcn: start, len });
        if runs.len() > MAX_RUNS {
            return invalid("too many runs");
        }
        at += 1 + len_size + off_size;
    }
}

/// Read and check MFT record `number`, given the bytes read for it; undoes the update sequence.
fn check_record(mut rec: Vec<u8>, number: u64) -> Result<Vec<u8>> {
    if rec.get(0..4) != Some(b"FILE") {
        return invalid("MFT record signature");
    }
    let usa_off = usize::from(u16_at(&rec, 4)?);
    let usa_count = usize::from(u16_at(&rec, 6)?);
    if usa_count != rec.len() / FIXUP_STRIDE + 1 || usa_off % 2 != 0 || usa_off < 0x30 {
        return invalid("MFT record update sequence");
    }
    let first_attr = usize::from(u16_at(&rec, 0x14)?);
    if usa_off + 2 * usa_count > first_attr || first_attr % 8 != 0 {
        return invalid("MFT record header");
    }
    let usn = get::<2>(&rec, usa_off)?;
    for i in 1..usa_count {
        let tail = i * FIXUP_STRIDE - 2;
        if get::<2>(&rec, tail)? != usn {
            return invalid("torn MFT record (update sequence mismatch)");
        }
        let fix = get::<2>(&rec, usa_off + 2 * i)?;
        rec[tail..tail + 2].copy_from_slice(&fix);
    }
    let flags = u16_at(&rec, 0x16)?;
    let used = u32_at(&rec, 0x18)? as usize;
    let allocated = u32_at(&rec, 0x1C)? as usize;
    if flags & 1 == 0 || allocated != rec.len() || used > rec.len() || first_attr >= used {
        return invalid("MFT record header");
    }
    if u64_at(&rec, 0x20)? != 0 {
        return invalid("MFT record is not a base record");
    }
    if u64::from(u32_at(&rec, 0x2C)?) != number {
        return invalid("MFT record number");
    }
    rec.truncate(used);
    Ok(rec)
}

/// The attributes of a checked record: (type, bytes of the whole attribute).
fn attributes(rec: &[u8]) -> Result<Vec<(u32, &[u8])>> {
    let mut at = usize::from(u16_at(rec, 0x14)?);
    let mut found = Vec::new();
    loop {
        let kind = u32_at(rec, at)?;
        if kind == ATTR_END {
            return Ok(found);
        }
        let len = u32_at(rec, at + 4)? as usize;
        if len < 24 || len % 8 != 0 || at.checked_add(len).is_none_or(|end| end > rec.len()) {
            return invalid("attribute bounds");
        }
        let attr = &rec[at..at + len];
        let name_len = usize::from(u8_at(attr, 9)?);
        let name_off = usize::from(u16_at(attr, 10)?);
        if name_len > 0 && name_off + 2 * name_len > len {
            return invalid("attribute name bounds");
        }
        if kind == ATTR_ATTRIBUTE_LIST {
            return invalid("attribute list (fragmented system file)");
        }
        found.push((kind, attr));
        at += len;
    }
}

/// The only attribute of `kind` without a name.
fn unnamed<'a>(attrs: &[(u32, &'a [u8])], kind: u32) -> Result<&'a [u8]> {
    let mut matching = attrs
        .iter()
        .filter(|(k, a)| *k == kind && a.get(9) == Some(&0));
    match (matching.next(), matching.next()) {
        (Some((_, a)), None) => Ok(a),
        (None, _) => invalid("attribute missing"),
        _ => invalid("attribute repeated"),
    }
}

fn resident_value(attr: &[u8]) -> Result<&[u8]> {
    if u8_at(attr, 8)? != 0 {
        return invalid("attribute should be resident");
    }
    let len = u32_at(attr, 16)? as usize;
    let off = usize::from(u16_at(attr, 20)?);
    attr.get(off..off.saturating_add(len))
        .ok_or(NtfsError::Invalid("resident value bounds"))
}

fn non_resident(attr: &[u8], geo: &Geometry) -> Result<Stream> {
    if u8_at(attr, 8)? != 1 || attr.len() < 64 {
        return invalid("attribute should be non-resident");
    }
    if u16_at(attr, 12)? & ATTR_FLAGS_UNSUPPORTED != 0 || u16_at(attr, 34)? != 0 {
        return invalid("compressed, encrypted or sparse system file");
    }
    if u64_at(attr, 16)? != 0 {
        return invalid("attribute does not start at VCN 0");
    }
    let last_vcn = u64_at(attr, 24)?;
    let runlist = usize::from(u16_at(attr, 32)?);
    let allocated = u64_at(attr, 40)?;
    let data_size = u64_at(attr, 48)?;
    let initialized = u64_at(attr, 56)?;
    if runlist < 64 || runlist >= attr.len() {
        return invalid("runlist bounds");
    }
    let runs = parse_runlist(&attr[runlist..], geo)?;
    let clusters = runs.iter().try_fold(0u64, |n, r| n.checked_add(r.len));
    if clusters.is_none_or(|n| last_vcn.checked_add(1) != Some(n))
        || clusters.and_then(|n| n.checked_mul(geo.cluster)) != Some(allocated)
        || data_size > allocated
        || initialized > data_size
    {
        return invalid("attribute sizes disagree with its runs");
    }
    Ok(Stream {
        runs,
        data_size,
        initialized,
    })
}

/// Read `out.len()` bytes of `stream` starting at byte `pos`, within its initialized part.
fn read_stream(
    stream: &Stream,
    geo: &Geometry,
    pos: u64,
    out: &mut [u8],
    read: &mut Reader,
) -> Result<()> {
    if pos
        .checked_add(out.len() as u64)
        .is_none_or(|end| end > stream.initialized)
    {
        return invalid("read past the initialized data");
    }
    let mut vcn_start = 0u64;
    let mut done = 0usize;
    for run in &stream.runs {
        if done == out.len() {
            break;
        }
        let run_bytes = run.len * geo.cluster;
        let run_start = vcn_start * geo.cluster;
        let want = pos + done as u64;
        if want < run_start + run_bytes {
            let within = want - run_start;
            let n = (run_bytes - within).min((out.len() - done) as u64) as usize;
            read(run.lcn * geo.cluster + within, &mut out[done..done + n])
                .map_err(NtfsError::Read)?;
            done += n;
        }
        vcn_start += run.len;
    }
    if done != out.len() {
        return invalid("stream shorter than its size");
    }
    Ok(())
}

fn read_record(mft: &Stream, geo: &Geometry, number: u64, read: &mut Reader) -> Result<Vec<u8>> {
    let mut rec = vec![0u8; geo.record];
    read_stream(mft, geo, number * geo.record as u64, &mut rec, read)?;
    check_record(rec, number)
}

/// The allocation bitmap of the NTFS file system on a volume of `volume_len` bytes, read through
/// `read`. The volume must not change while this runs (locked and dismounted).
pub fn read_bitmap(volume_len: u64, read: &mut Reader) -> Result<Bitmap> {
    let mut boot = [0u8; 512];
    read(0, &mut boot).map_err(NtfsError::Read)?;
    let geo = parse_boot(&boot, volume_len)?;

    // Record 0 describes the MFT itself; it sits at the start of the MFT's first extent.
    let mut rec0 = vec![0u8; geo.record];
    read(geo.mft_lcn * geo.cluster, &mut rec0).map_err(NtfsError::Read)?;
    let rec0 = check_record(rec0, RECORD_MFT)?;
    let mft = non_resident(unnamed(&attributes(&rec0)?, ATTR_DATA)?, &geo)?;
    if mft.runs.first().map(|r| r.lcn) != Some(geo.mft_lcn)
        || mft.initialized < (RECORD_BITMAP + 1) * geo.record as u64
    {
        return invalid("MFT runs disagree with the boot sector");
    }

    // A volume marked dirty may have a bitmap that doesn't match its files: chkdsk first.
    let rec3 = read_record(&mft, &geo, RECORD_VOLUME, read)?;
    let info = resident_value(unnamed(&attributes(&rec3)?, ATTR_VOLUME_INFORMATION)?)?;
    if u8_at(info, 8)? != 3 {
        return invalid("NTFS version");
    }
    if u16_at(info, 10)? & VOLUME_DIRTY != 0 {
        return Err(NtfsError::Dirty);
    }

    let rec6 = read_record(&mft, &geo, RECORD_BITMAP, read)?;
    let bitmap = non_resident(unnamed(&attributes(&rec6)?, ATTR_DATA)?, &geo)?;
    let needed = geo.clusters.div_ceil(8);
    if needed > MAX_BITMAP_BYTES {
        return invalid("bitmap too large");
    }
    if bitmap.data_size < needed || bitmap.initialized < needed {
        return invalid("bitmap smaller than the cluster count");
    }
    let mut bits = vec![0u8; needed as usize];
    read_stream(&bitmap, &geo, 0, &mut bits, read)?;
    let result = Bitmap {
        cluster: geo.cluster,
        clusters: geo.clusters,
        bits,
    };

    // Cross-check: the boot sector, the MFT and the bitmap itself must all be in use.
    let mut system = mft.runs.iter().chain(&bitmap.runs).copied();
    if !result.is_used(0) || !system.all(|r| (r.lcn..r.lcn + r.len).all(|lcn| result.is_used(lcn)))
    {
        return invalid("bitmap marks system files free");
    }
    Ok(result)
}

/// Free runs of at least `min_bytes` in `bitmap`, as absolute disk byte ranges for a volume
/// whose cluster 0 is at disk byte `offset`.
pub fn free_runs(bitmap: &Bitmap, offset: u64, min_bytes: u64) -> Vec<(u64, u64)> {
    let min = min_bytes.div_ceil(bitmap.cluster).max(1);
    let clusters = bitmap.clusters.min(bitmap.bits.len() as u64 * 8);
    let mut runs = Vec::new();
    let mut lcn = 0u64;
    while lcn < clusters {
        if lcn % 8 == 0 && bitmap.bits[(lcn / 8) as usize] == 0xFF {
            lcn += 8;
            continue;
        }
        if bitmap.is_used(lcn) {
            lcn += 1;
            continue;
        }
        let start = lcn;
        while lcn < clusters {
            if lcn % 8 == 0 && lcn + 8 <= clusters && bitmap.bits[(lcn / 8) as usize] == 0 {
                lcn += 8;
            } else if !bitmap.is_used(lcn) {
                lcn += 1;
            } else {
                break;
            }
        }
        if lcn - start >= min {
            runs.push((
                offset + start * bitmap.cluster,
                (lcn - start) * bitmap.cluster,
            ));
        }
    }
    runs
}

/// A cluster is free only if every bitmap says so. Bitmaps of different geometry mean the reads
/// don't describe the same file system, so nothing can be trusted.
pub fn intersect_free(a: &Bitmap, b: &Bitmap) -> Option<Bitmap> {
    if a.cluster != b.cluster || a.clusters != b.clusters {
        return None;
    }
    let len = a.clusters.div_ceil(8) as usize;
    let bits = (0..len)
        .map(|i| a.bits.get(i).copied().unwrap_or(0xFF) | b.bits.get(i).copied().unwrap_or(0xFF))
        .collect();
    Some(Bitmap {
        cluster: a.cluster,
        clusters: a.clusters,
        bits,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Builds small NTFS images with just the structures the parser reads.
    struct Image {
        bytes: Vec<u8>,
        sector: u64,
        cluster: u64,
        record: usize,
        clusters: u64,
        mft_runs: Vec<(u64, u64)>,
        bitmap_runs: Vec<(u64, u64)>,
        /// Clusters in use besides the system files.
        used: Vec<(u64, u64)>,
        dirty: bool,
    }

    impl Image {
        /// 8 MiB, 4 KiB clusters, 1 KiB records, MFT and bitmap in one extent each.
        fn simple() -> Self {
            Self {
                bytes: Vec::new(),
                sector: 512,
                cluster: 4096,
                record: 1024,
                clusters: 2048,
                mft_runs: vec![(16, 4)],
                bitmap_runs: vec![(20, 1)],
                used: vec![(100, 50), (1000, 1)],
                dirty: false,
            }
        }

        fn build(mut self) -> Self {
            let len = (self.clusters * self.cluster) as usize + self.sector as usize;
            self.bytes = vec![0u8; len];
            self.boot();
            let mft = self.mft_runs.clone();
            let bitmap_runs = self.bitmap_runs.clone();
            let record = self.record;
            let mft_alloc = mft.iter().map(|r| r.1).sum::<u64>() * self.cluster;
            let rec0 = self.record_with(
                0,
                &[nonresident_attr(ATTR_DATA, &mft, self.cluster, mft_alloc)],
            );
            let mut info = vec![0u8; 12];
            info[8] = 3;
            info[9] = 1;
            info[10] = u8::from(self.dirty);
            let rec3 = self.record_with(3, &[resident_attr(ATTR_VOLUME_INFORMATION, &info)]);
            let bitmap_size = self.clusters.div_ceil(8).next_multiple_of(8);
            let rec6 = self.record_with(
                6,
                &[nonresident_attr(
                    ATTR_DATA,
                    &bitmap_runs,
                    self.cluster,
                    bitmap_size,
                )],
            );
            for (n, rec) in [(0, rec0), (3, rec3), (6, rec6)] {
                self.write_stream(&mft, n * record as u64, &rec);
            }
            let mut bits = vec![0u8; bitmap_size as usize];
            let mut mark = |lcn: u64, len: u64| {
                for c in lcn..lcn + len {
                    bits[(c / 8) as usize] |= 1 << (c % 8);
                }
            };
            mark(0, 1);
            for &(lcn, len) in mft.iter().chain(&bitmap_runs).chain(&self.used) {
                mark(lcn, len);
            }
            self.write_stream(&bitmap_runs, 0, &bits);
            self
        }

        fn boot(&mut self) {
            let b = &mut self.bytes;
            b[3..11].copy_from_slice(b"NTFS    ");
            b[11..13].copy_from_slice(&(self.sector as u16).to_le_bytes());
            let spc = self.cluster / self.sector;
            b[13] = if spc <= 0x80 {
                spc as u8
            } else {
                (256 - spc.trailing_zeros()) as u8
            };
            let total_sectors = self.clusters * spc;
            b[40..48].copy_from_slice(&total_sectors.to_le_bytes());
            b[48..56].copy_from_slice(&self.mft_runs[0].0.to_le_bytes());
            b[56..64].copy_from_slice(&2u64.to_le_bytes());
            b[64] = if self.record as u64 >= self.cluster {
                (self.record as u64 / self.cluster) as u8
            } else {
                (-(self.record.trailing_zeros() as i8)) as u8
            };
            b[510..512].copy_from_slice(&0xAA55u16.to_le_bytes());
        }

        /// A FILE record with the given attributes and the update sequence applied.
        fn record_with(&self, number: u32, attrs: &[Vec<u8>]) -> Vec<u8> {
            let mut r = vec![0u8; self.record];
            let usa_count = self.record / FIXUP_STRIDE + 1;
            let first = (0x30 + 2 * usa_count).next_multiple_of(8);
            r[0..4].copy_from_slice(b"FILE");
            r[4..6].copy_from_slice(&0x30u16.to_le_bytes());
            r[6..8].copy_from_slice(&(usa_count as u16).to_le_bytes());
            r[0x14..0x16].copy_from_slice(&(first as u16).to_le_bytes());
            r[0x16..0x18].copy_from_slice(&1u16.to_le_bytes());
            r[0x2C..0x30].copy_from_slice(&number.to_le_bytes());
            let mut at = first;
            for a in attrs {
                r[at..at + a.len()].copy_from_slice(a);
                at += a.len();
            }
            r[at..at + 4].copy_from_slice(&ATTR_END.to_le_bytes());
            at += 8;
            r[0x18..0x1C].copy_from_slice(&(at as u32).to_le_bytes());
            r[0x1C..0x20].copy_from_slice(&(self.record as u32).to_le_bytes());
            apply_fixups(&mut r, 0x4242);
            r
        }

        fn write_stream(&mut self, runs: &[(u64, u64)], pos: u64, data: &[u8]) {
            for (i, &byte) in data.iter().enumerate() {
                let mut at = pos + i as u64;
                for &(lcn, len) in runs {
                    let bytes = len * self.cluster;
                    if at < bytes {
                        self.bytes[(lcn * self.cluster + at) as usize] = byte;
                        break;
                    }
                    at -= bytes;
                }
            }
        }

        /// Byte offset of MFT record `n` on the volume (records here never straddle runs
        /// unless a test says so).
        fn record_offset(&self, n: u64) -> usize {
            let mut at = n * self.record as u64;
            for &(lcn, len) in &self.mft_runs {
                if at < len * self.cluster {
                    return (lcn * self.cluster + at) as usize;
                }
                at -= len * self.cluster;
            }
            unreachable!()
        }

        fn parse(&self) -> Result<Bitmap> {
            parse_bytes(&self.bytes)
        }

        fn expected_used(&self, lcn: u64) -> bool {
            lcn == 0
                || self
                    .mft_runs
                    .iter()
                    .chain(&self.bitmap_runs)
                    .chain(&self.used)
                    .any(|&(s, l)| (s..s + l).contains(&lcn))
        }

        /// Rewrite record `n` after `edit` changes its unprotected bytes.
        fn edit_record(&mut self, n: u64, edit: impl FnOnce(&mut Vec<u8>)) {
            let at = self.record_offset(n);
            let mut rec = self.bytes[at..at + self.record].to_vec();
            undo_fixups(&mut rec);
            edit(&mut rec);
            apply_fixups(&mut rec, 0x4343);
            self.bytes[at..at + self.record].copy_from_slice(&rec);
        }
    }

    fn parse_bytes(bytes: &[u8]) -> Result<Bitmap> {
        read_bitmap(bytes.len() as u64, &mut |at, buf: &mut [u8]| {
            let src = usize::try_from(at)
                .ok()
                .and_then(|a| bytes.get(a..a.checked_add(buf.len())?))
                .ok_or(IoError::OutOfRange)?;
            buf.copy_from_slice(src);
            Ok(())
        })
    }

    fn apply_fixups(r: &mut [u8], usn: u16) {
        let count = r.len() / FIXUP_STRIDE + 1;
        r[0x30..0x32].copy_from_slice(&usn.to_le_bytes());
        for i in 1..count {
            let tail = i * FIXUP_STRIDE - 2;
            let saved = [r[tail], r[tail + 1]];
            r[0x30 + 2 * i..0x32 + 2 * i].copy_from_slice(&saved);
            r[tail..tail + 2].copy_from_slice(&usn.to_le_bytes());
        }
    }

    fn undo_fixups(r: &mut [u8]) {
        let count = r.len() / FIXUP_STRIDE + 1;
        for i in 1..count {
            let tail = i * FIXUP_STRIDE - 2;
            let saved = [r[0x30 + 2 * i], r[0x31 + 2 * i]];
            r[tail..tail + 2].copy_from_slice(&saved);
        }
    }

    fn resident_attr(kind: u32, value: &[u8]) -> Vec<u8> {
        let len = (24 + value.len()).next_multiple_of(8);
        let mut a = vec![0u8; len];
        a[0..4].copy_from_slice(&kind.to_le_bytes());
        a[4..8].copy_from_slice(&(len as u32).to_le_bytes());
        a[16..20].copy_from_slice(&(value.len() as u32).to_le_bytes());
        a[20..22].copy_from_slice(&24u16.to_le_bytes());
        a[24..24 + value.len()].copy_from_slice(value);
        a
    }

    fn encode_runs(runs: &[(u64, u64)]) -> Vec<u8> {
        fn minimal(v: i64) -> Vec<u8> {
            let bytes = v.to_le_bytes();
            let mut n = 8;
            while n > 1 && signed_le(&bytes[..n - 1]) == v {
                n -= 1;
            }
            bytes[..n].to_vec()
        }
        let mut out = Vec::new();
        let mut prev = 0i64;
        for &(lcn, len) in runs {
            let l = minimal(len as i64);
            let o = minimal(lcn as i64 - prev);
            prev = lcn as i64;
            out.push((o.len() << 4 | l.len()) as u8);
            out.extend(l);
            out.extend(o);
        }
        out.push(0);
        out
    }

    fn nonresident_attr(kind: u32, runs: &[(u64, u64)], cluster: u64, size: u64) -> Vec<u8> {
        let list = encode_runs(runs);
        let len = (64 + list.len()).next_multiple_of(8);
        let clusters: u64 = runs.iter().map(|r| r.1).sum();
        let mut a = vec![0u8; len];
        a[0..4].copy_from_slice(&kind.to_le_bytes());
        a[4..8].copy_from_slice(&(len as u32).to_le_bytes());
        a[8] = 1;
        a[24..32].copy_from_slice(&(clusters - 1).to_le_bytes());
        a[32..34].copy_from_slice(&64u16.to_le_bytes());
        a[40..48].copy_from_slice(&(clusters * cluster).to_le_bytes());
        a[48..56].copy_from_slice(&size.to_le_bytes());
        a[56..64].copy_from_slice(&size.to_le_bytes());
        a[64..64 + list.len()].copy_from_slice(&list);
        a
    }

    /// Offset of the first attribute of `kind` in a (fixed-up) record.
    fn attr_offset(rec: &[u8], kind: u32) -> usize {
        let mut at = usize::from(u16::from_le_bytes([rec[0x14], rec[0x15]]));
        while u32::from_le_bytes(rec[at..at + 4].try_into().unwrap()) != kind {
            at += u32::from_le_bytes(rec[at + 4..at + 8].try_into().unwrap()) as usize;
        }
        at
    }

    fn assert_matches(img: &Image, bitmap: &Bitmap) {
        assert_eq!(bitmap.cluster, img.cluster);
        assert_eq!(bitmap.clusters, img.clusters);
        for lcn in 0..img.clusters {
            assert_eq!(bitmap.is_used(lcn), img.expected_used(lcn), "cluster {lcn}");
        }
    }

    fn refused(img: &Image, why: &str) {
        match img.parse() {
            Err(NtfsError::Invalid(w)) => {
                assert!(w.contains(why), "refused for {w:?}, want {why:?}")
            }
            other => panic!("expected refusal ({why}), got {other:?}"),
        }
    }

    #[test]
    fn reads_the_bitmap_of_a_simple_volume() {
        let img = Image::simple().build();
        let bitmap = img.parse().unwrap();
        assert_matches(&img, &bitmap);
        let runs = free_runs(&bitmap, 1 << 20, 0);
        assert_eq!(runs.first(), Some(&((1 << 20) + 4096, 15 * 4096)));
        let free: u64 = runs.iter().map(|r| r.1).sum();
        assert_eq!(free, (2048 - 1 - 4 - 1 - 51) * 4096);
    }

    #[test]
    fn fragmented_mft_with_records_straddling_runs() {
        // 512-byte clusters: each 1 KiB record spans two clusters, record 6 across two runs.
        let mut img = Image {
            cluster: 512,
            clusters: 16384,
            mft_runs: vec![(40, 13), (9000, 3), (500, 20)],
            bitmap_runs: vec![(3000, 1), (60, 2), (12000, 1)],
            used: vec![(16000, 300)],
            ..Image::simple()
        }
        .build();
        assert_matches(&img, &img.parse().unwrap());
        // Damage the second half of record 6 (in the second run): the fixups catch it.
        img.bytes[9000 * 512 + 510] ^= 0xFF;
        refused(&img, "update sequence");
    }

    #[test]
    fn four_k_sectors_and_large_clusters() {
        let img = Image {
            sector: 4096,
            cluster: 4096,
            record: 4096,
            mft_runs: vec![(16, 8)],
            bitmap_runs: vec![(30, 1)],
            ..Image::simple()
        }
        .build();
        assert_matches(&img, &img.parse().unwrap());
        let img = Image {
            cluster: 128 << 10,
            clusters: 256,
            mft_runs: vec![(4, 1)],
            bitmap_runs: vec![(5, 1)],
            used: vec![(100, 3)],
            ..Image::simple()
        }
        .build();
        assert_matches(&img, &img.parse().unwrap());
    }

    #[test]
    fn a_dirty_volume_is_refused() {
        let img = Image {
            dirty: true,
            ..Image::simple()
        }
        .build();
        assert_eq!(img.parse(), Err(NtfsError::Dirty));
    }

    #[test]
    fn layout_surprises_are_refused() {
        let base = || Image::simple().build();

        let mut img = base();
        img.bytes[3] = b'X';
        refused(&img, "boot sector");

        let mut img = base();
        let too_big = (img.bytes.len() as u64 / 512 + 1).to_le_bytes();
        img.bytes[40..48].copy_from_slice(&too_big);
        refused(&img, "larger than the volume");

        let mut img = base();
        img.bytes[13] = 3;
        refused(&img, "sectors per cluster");

        let mut img = base();
        let at = img.record_offset(6) + 0x2C;
        img.bytes[at] = 7;
        refused(&img, "record number");

        let mut img = base();
        img.edit_record(6, |r| {
            let a = attr_offset(r, ATTR_DATA);
            r[a + 12] = 0x01; // compressed
        });
        refused(&img, "compressed");

        let mut img = base();
        img.edit_record(6, |r| {
            let a = attr_offset(r, ATTR_DATA);
            r[a + 64] = 0x01; // length only: a sparse run
        });
        refused(&img, "sparse");

        let mut img = base();
        img.edit_record(6, |r| {
            let a = attr_offset(r, ATTR_DATA);
            r[a + 56..a + 64].copy_from_slice(&100u64.to_le_bytes()); // initialized size
        });
        refused(&img, "bitmap smaller");

        let mut img = base();
        img.edit_record(6, |r| {
            let a = attr_offset(r, ATTR_DATA);
            r[a + 24] = 1; // last VCN past the runs
        });
        refused(&img, "sizes disagree");

        let mut img = base();
        img.edit_record(6, |r| {
            let a = attr_offset(r, ATTR_DATA);
            // One run of 1 cluster at 0x7FFF, far outside the volume.
            r[a + 64] = 0x21;
            r[a + 66] = 0xFF;
            r[a + 67] = 0x7F;
        });
        refused(&img, "outside the volume");

        let mut img = base();
        img.edit_record(6, |r| {
            let a = attr_offset(r, ATTR_DATA);
            r[a..a + 4].copy_from_slice(&ATTR_ATTRIBUTE_LIST.to_le_bytes());
        });
        refused(&img, "attribute list");

        let mut img = base();
        img.edit_record(6, |r| {
            let a = attr_offset(r, ATTR_DATA);
            r[a + 4] = 0xF0; // runs past the record
        });
        refused(&img, "attribute bounds");

        let mut img = base();
        img.edit_record(0, |r| {
            let a = attr_offset(r, ATTR_DATA);
            r[a + 9] = 4; // the MFT's data now has a name, so there is no unnamed one
            r[a + 10] = 64;
        });
        refused(&img, "missing");

        // The bitmap itself claims the MFT is free: not believable.
        let mut img = base();
        img.bytes[20 * 4096 + 2] = 0;
        refused(&img, "system files free");
    }

    #[test]
    fn read_errors_are_reported_not_guessed() {
        let img = Image::simple().build();
        let bytes = &img.bytes;
        let mut calls = 0;
        let err = read_bitmap(bytes.len() as u64, &mut |at, buf: &mut [u8]| {
            calls += 1;
            if calls == 3 {
                return Err(IoError::Os(21));
            }
            buf.copy_from_slice(&bytes[at as usize..at as usize + buf.len()]);
            Ok(())
        })
        .unwrap_err();
        assert_eq!(err, NtfsError::Read(IoError::Os(21)));
        assert!(err.to_string().contains("0x00000015"));
    }

    struct XorShift(u64);
    impl XorShift {
        fn next(&mut self) -> u64 {
            self.0 ^= self.0 << 13;
            self.0 ^= self.0 >> 7;
            self.0 ^= self.0 << 17;
            self.0
        }
    }

    #[test]
    fn mutated_metadata_never_panics_or_frees_used_clusters() {
        let clean = Image {
            cluster: 512,
            clusters: 16384,
            mft_runs: vec![(40, 13), (9000, 3), (500, 20)],
            bitmap_runs: vec![(3000, 1), (60, 2), (12000, 1)],
            used: vec![(16000, 300), (7000, 9)],
            ..Image::simple()
        }
        .build();
        // Everything the parser reads except the bitmap contents.
        let mut targets: Vec<usize> = (0..512).collect();
        for n in [0, 3, 6] {
            let at = clean.record_offset(n);
            targets.extend(at..at + 512);
            let half = clean.record_offset(n) + 512;
            let second = if n == 6 { 9000 * 512 } else { half };
            targets.extend(second..second + 512);
        }
        let truth = clean.parse().unwrap();
        let mut rng = XorShift(0x9E37_79B9_7F4A_7C15);
        let (mut accepted, mut refused) = (0, 0);
        let mut bytes = clean.bytes.clone();
        for _ in 0..20_000 {
            let flips: Vec<(usize, u8)> = (0..1 + rng.next() % 4)
                .map(|_| {
                    let at = targets[(rng.next() % targets.len() as u64) as usize];
                    (at, 1 << (rng.next() % 8))
                })
                .collect();
            for &(at, bit) in &flips {
                bytes[at] ^= bit;
            }
            let result = parse_bytes(&bytes);
            for &(at, bit) in flips.iter().rev() {
                bytes[at] ^= bit;
            }
            match result {
                Ok(bitmap) => {
                    accepted += 1;
                    let whole = (bitmap.clusters / 8) as usize;
                    for (i, (&got, &want)) in bitmap.bits.iter().zip(&truth.bits).enumerate() {
                        let mask = if i < whole {
                            0xFF
                        } else {
                            (1u8 << (bitmap.clusters % 8)) - 1
                        };
                        assert_eq!(
                            want & !got & mask,
                            0,
                            "used clusters reported free near byte {i}"
                        );
                    }
                }
                Err(_) => refused += 1,
            }
        }
        assert!(
            accepted > 0 && refused > 0,
            "{accepted} accepted, {refused} refused"
        );
    }

    #[test]
    fn garbage_never_panics() {
        let mut rng = XorShift(12345);
        for round in 0..2_000 {
            let mut bytes: Vec<u8> = (0..64 * 1024).map(|_| rng.next() as u8).collect();
            if round % 2 == 0 {
                // A plausible boot sector over random metadata.
                let img = Image::simple().build();
                bytes[..512].copy_from_slice(&img.bytes[..512]);
            }
            let len = 8 << 20;
            let _ = read_bitmap(len, &mut |at, buf: &mut [u8]| {
                for (i, b) in buf.iter_mut().enumerate() {
                    *b = bytes[(at as usize + i) % bytes.len()];
                }
                Ok(())
            });
        }
    }

    #[test]
    fn free_runs_respect_the_minimum_and_the_cluster_count() {
        let bitmap = Bitmap {
            cluster: 4096,
            clusters: 20,
            // Used: 0, 9..=10; clusters 20..24 don't exist even though their bits are clear.
            bits: vec![0b0000_0001, 0b0000_0110, 0b0000_0000],
        };
        assert_eq!(
            free_runs(&bitmap, 0, 0),
            vec![(4096, 8 * 4096), (11 * 4096, 9 * 4096)]
        );
        assert_eq!(free_runs(&bitmap, 0, 9 * 4096), vec![(11 * 4096, 9 * 4096)]);
    }

    #[test]
    fn intersection_keeps_clusters_used_in_either() {
        let a = Bitmap {
            cluster: 4096,
            clusters: 16,
            bits: vec![0b0000_0001, 0b0000_0000],
        };
        let b = Bitmap {
            bits: vec![0b0000_0000, 0b1000_0000],
            ..a.clone()
        };
        assert_eq!(intersect_free(&a, &b).unwrap().bits, vec![1, 0x80]);
        let other = Bitmap {
            cluster: 512,
            ..a.clone()
        };
        assert!(intersect_free(&a, &other).is_none());
    }
}
