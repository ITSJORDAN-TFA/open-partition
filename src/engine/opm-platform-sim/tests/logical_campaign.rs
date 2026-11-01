//! Crash campaigns for moving one MBR logical drive inside its extended partition.
//!
//! The data disk holds a real MBR and EBR chain. Its table is read and written by the same
//! `Layout` code the engine uses on Windows, and written back the way
//! `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` does: every table sector, then one flush. Power can fail
//! between any two of those writes and the disk cache may keep any subset of them, so every
//! partial rewrite of the chain is tried.

#![cfg(windows)]

use opm_core::journal::load_newest;
use opm_core::{
    recover, roll_back, start_move, BlockDevice, DeviceJournal, DiskIdentity, IoError, MoveError,
    MoveJob, PartitionEntry, PartitionKey, PartitionTable, Phase, RangeDigest,
};
use opm_platform_sim::{run_campaign, CampaignOptions, LossModel, Power, SimDisk, SimWorld};
use opm_platform_win32::{insert_and_write_ebr, Layout};

const SECTOR: u32 = 512;
const S: u64 = SECTOR as u64;
const DISK_SECTORS: u64 = 256;
const IDENTITY: DiskIdentity = DiskIdentity {
    id: *b"logical-disk-001",
};

/// The extended partition: sectors 8..248.
const CONTAINER: (u64, u64) = (8, 240);
/// The logical drives as `(EBR, start, length)` in sectors. The first EBR is the container
/// start; gaps of free space between them.
const LOGICALS: [(u64, u64, u64); 3] = [(8, 10, 16), (40, 42, 16), (80, 82, 16)];

/// How the table is written back.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Attach {
    /// What the engine does: the new EBR first, durably.
    WriteEbrFirst,
    /// Without that step, to show the campaign catches it.
    TableOnly,
}

#[derive(Debug)]
struct MbrDisk {
    disk: SimDisk,
    attach: Attach,
}

impl BlockDevice for MbrDisk {
    fn sector_size(&self) -> u32 {
        self.disk.sector_size()
    }
    fn size_bytes(&self) -> u64 {
        self.disk.size_bytes()
    }
    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<(), IoError> {
        self.disk.read_at(offset, buf)
    }
    fn write_at(&mut self, offset: u64, data: &[u8]) -> Result<(), IoError> {
        self.disk.write_at(offset, data)
    }
    fn flush(&mut self) -> Result<(), IoError> {
        self.disk.flush()
    }
}

impl MbrDisk {
    fn layout(&mut self) -> Result<Layout, IoError> {
        let disk = &mut self.disk;
        Layout::read_mbr(SECTOR, |at| {
            let mut sector = vec![0; SECTOR as usize];
            disk.read_at(at, &mut sector)?;
            Ok(sector)
        })
    }

    /// Every table sector in chain order, then one flush.
    fn write_layout(&mut self, layout: &Layout) -> Result<(), IoError> {
        for (at, sector) in layout.table_sectors(SECTOR) {
            self.disk.write_at(at, &sector)?;
        }
        self.disk.flush()
    }
}

impl PartitionTable for MbrDisk {
    fn partition_start(&mut self, partition: PartitionKey) -> Result<Option<u64>, IoError> {
        Ok(self.layout()?.find(partition).map(|e| e.offset))
    }

    fn entry(&mut self, partition: PartitionKey) -> Result<PartitionEntry, IoError> {
        let layout = self.layout()?;
        let entry = layout.find(partition).ok_or(IoError::Os(1168))?;
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
        match self.attach {
            Attach::WriteEbrFirst => {
                insert_and_write_ebr(self, &mut layout, partition, entry, start)?
            }
            Attach::TableOnly => layout.insert(partition, entry, start, SECTOR)?,
        }
        self.write_layout(&layout)
    }

    fn range_is_free(
        &mut self,
        partition: PartitionKey,
        start: u64,
        length: u64,
    ) -> Result<bool, IoError> {
        Ok(self.layout()?.range_is_free_for(partition, start, length))
    }
}

impl RangeDigest for MbrDisk {
    fn digest(&mut self, parts: &[&[u8]]) -> [u8; 32] {
        use sha2::{Digest, Sha256};
        let mut hasher = Sha256::new();
        for part in parts {
            hasher.update(part);
        }
        hasher.finalize().into()
    }
}

#[derive(Debug)]
struct Machine {
    data: MbrDisk,
    journal: DeviceJournal<SimDisk>,
}

impl SimWorld for Machine {
    fn power(&self) -> &Power {
        self.data.disk.power()
    }

    fn power_cycle(&self, model: LossModel) -> Self {
        let power = Power::new();
        Machine {
            data: MbrDisk {
                disk: self.data.disk.power_cycle_on(&power, model.for_disk(0)),
                attach: self.data.attach,
            },
            journal: DeviceJournal::new(
                self.journal
                    .device()
                    .power_cycle_on(&power, model.for_disk(1)),
                0,
            )
            .unwrap(),
        }
    }
}

/// One partition table entry as stored on disk.
fn put_entry(image: &mut [u8], table: u64, slot: usize, kind: u8, rel: u64, sectors: u64) {
    let t = (table * S) as usize + 446 + 16 * slot;
    image[t + 4] = kind;
    image[t + 8..t + 12].copy_from_slice(&(rel as u32).to_le_bytes());
    image[t + 12..t + 16].copy_from_slice(&(sectors as u32).to_le_bytes());
}

fn seal(image: &mut [u8], table: u64) {
    let t = (table * S) as usize;
    image[t + 510] = 0x55;
    image[t + 511] = 0xAA;
}

/// Contents of logical drive `id`: depends on the drive and the position in it, so data in
/// the wrong place or a stale copy is caught.
fn data_byte(id: usize, i: usize) -> u8 {
    (i.wrapping_mul(31) ^ (i >> 9).wrapping_mul(17) ^ id.wrapping_mul(0x3B) ^ 0x5A) as u8
}

/// Moves are in sectors.
#[derive(Debug, Clone, Copy)]
struct Scenario {
    /// Which logical drive moves (0, 1 or 2).
    which: usize,
    to: u64,
    chunk: u64,
    attach: Attach,
}

impl Scenario {
    fn from(&self) -> u64 {
        LOGICALS[self.which].1
    }

    fn length(&self) -> u64 {
        LOGICALS[self.which].2
    }

    fn key(&self) -> PartitionKey {
        Layout::logical_key(self.from() * S, self.to * S).unwrap()
    }

    fn job(&self) -> MoveJob {
        MoveJob {
            operation_id: *b"logical-move-001",
            partition: self.key(),
            source_offset: self.from() * S,
            target_offset: self.to * S,
            length: self.length() * S,
            chunk_size: self.chunk * S,
            verify: true,
        }
    }

    fn setup(&self) -> Machine {
        let size = (DISK_SECTORS * S) as usize;
        // Background bytes never look like a partition table.
        let mut image: Vec<u8> = (0..size).map(|i| (i % 7) as u8 | 0x80).collect();
        image[..S as usize].fill(0);
        image[440..444].copy_from_slice(&0x0BAD_F00Du32.to_le_bytes());
        put_entry(&mut image, 0, 0, 0x0F, CONTAINER.0, CONTAINER.1);
        seal(&mut image, 0);
        for (id, &(ebr, start, length)) in LOGICALS.iter().enumerate() {
            image[(ebr * S) as usize..((ebr + 1) * S) as usize].fill(0);
            put_entry(&mut image, ebr, 0, 0x07, start - ebr, length);
            if let Some(&(next, next_start, next_length)) = LOGICALS.get(id + 1) {
                let link = next_start + next_length - next;
                put_entry(&mut image, ebr, 1, 0x05, next - CONTAINER.0, link);
            }
            seal(&mut image, ebr);
            let at = (start * S) as usize;
            for i in 0..(length * S) as usize {
                image[at + i] = data_byte(id, i);
            }
        }
        let power = Power::new();
        Machine {
            data: MbrDisk {
                disk: SimDisk::from_image_on_rail(&power, SECTOR, image),
                attach: self.attach,
            },
            journal: DeviceJournal::new(SimDisk::on_rail(&power, SECTOR, 4 * 4096), 0).unwrap(),
        }
    }

    fn operation(&self, m: &mut Machine) -> Result<(), MoveError> {
        start_move(&mut m.data, &mut m.journal, IDENTITY, &self.job(), &[]).map(|_| ())
    }

    fn recover(m: &mut Machine) -> Result<(), MoveError> {
        recover(&mut m.data, &mut m.journal, IDENTITY).map(|_| ())
    }

    /// The chain lists all three logical drives, the moved one wholly at its source or wholly
    /// at its target, in step with the journal, and every drive holds its own data.
    fn verify(&self, m: &mut Machine) -> Result<(), String> {
        let layout = m
            .data
            .layout()
            .map_err(|e| format!("table unreadable: {e}"))?;
        let mut found: Vec<(u64, u64)> = layout
            .entries()
            .iter()
            .filter(|e| layout.is_logical(e))
            .map(|e| (e.offset / S, e.length / S))
            .collect();
        found.sort_unstable();
        let at = |start: u64| {
            let mut all: Vec<(u64, u64)> = LOGICALS
                .iter()
                .enumerate()
                .map(|(id, l)| (if id == self.which { start } else { l.1 }, l.2))
                .collect();
            all.sort_unstable();
            all
        };
        let journal = load_newest(&mut m.journal).map_err(|e| format!("{e:?}"))?;
        let place = if found == at(self.to) {
            if journal.map(|r| r.phase) != Some(Phase::Done) {
                return Err(format!("chain updated but journal is {journal:?}"));
            }
            self.to
        } else if found == at(self.from()) {
            if journal.is_some_and(|r| r.phase != Phase::Done) {
                return Err(format!("move not finished: journal is {journal:?}"));
            }
            self.from()
        } else {
            return Err(format!("logical drives in the chain: {found:?}"));
        };
        let container = layout
            .entries()
            .into_iter()
            .find(|e| e.index < 4)
            .map(|e| (e.offset / S, e.length / S, e.mbr_type));
        if container != Some((CONTAINER.0, CONTAINER.1, Some(0x0F))) {
            return Err(format!("extended partition changed: {container:?}"));
        }
        for (id, &(_, start, length)) in LOGICALS.iter().enumerate() {
            let start = if id == self.which { place } else { start };
            let mut buf = vec![0; (length * S) as usize];
            m.data
                .read_at(start * S, &mut buf)
                .map_err(|e| e.to_string())?;
            if let Some(i) = (0..buf.len()).find(|&i| buf[i] != data_byte(id, i)) {
                return Err(format!(
                    "logical drive {id} at sector {start}: byte {i} wrong"
                ));
            }
        }
        Ok(())
    }

    fn campaign(self, options: CampaignOptions) -> opm_platform_sim::CampaignReport {
        run_campaign(
            || self.setup(),
            |m| self.operation(m),
            Self::recover,
            |m| self.verify(m),
            options,
        )
    }
}

fn scenario(which: usize, to: u64, chunk: u64) -> Scenario {
    Scenario {
        which,
        to,
        chunk,
        attach: Attach::WriteEbrFirst,
    }
}

fn options() -> CampaignOptions {
    CampaignOptions {
        random_seeds: 6,
        crash_during_recovery: false,
    }
}

#[test]
fn first_logical_right() {
    // Overlapping its source; its EBR (the container start) stays.
    let report = scenario(0, 20, 4).campaign(options());
    report.assert_passed();
    assert!(report.total_ops > 0);
}

#[test]
fn first_logical_left() {
    scenario(0, 9, 1).campaign(options()).assert_passed();
}

#[test]
fn middle_logical_right() {
    // Its new EBR lands in free space after its old data.
    scenario(1, 60, 6).campaign(options()).assert_passed();
}

#[test]
fn middle_logical_left_over_its_old_ebr() {
    // Overlapping its source, and the copy overwrites its old (unlinked) EBR.
    scenario(1, 30, 4).campaign(options()).assert_passed();
}

#[test]
fn last_logical_right() {
    scenario(2, 120, 8).campaign(options()).assert_passed();
}

#[test]
fn last_logical_left() {
    scenario(2, 70, 4).campaign(options()).assert_passed();
}

#[test]
fn crashes_during_recovery_are_survived() {
    for s in [scenario(0, 20, 5), scenario(1, 30, 6), scenario(2, 70, 6)] {
        s.campaign(CampaignOptions {
            random_seeds: 2,
            crash_during_recovery: true,
        })
        .assert_passed();
    }
}

#[test]
fn without_the_ebr_written_first_a_torn_table_write_loses_the_chain() {
    // The control: the same move with the EBR left to Windows' own table write. Some crash
    // leaves the previous EBR linking to a sector that isn't a table yet.
    let report = Scenario {
        attach: Attach::TableOnly,
        ..scenario(1, 60, 6)
    }
    .campaign(options());
    assert!(
        !report.passed(),
        "the campaign must catch a link written before its EBR"
    );
}

#[test]
fn rollback_puts_a_logical_drive_back_after_crashes_anywhere() {
    // Not overlapping, so it can be rolled back from any point before the chain is updated.
    let s = scenario(2, 120, 8);
    let roll_back_or_finish =
        |m: &mut Machine| match roll_back(&mut m.data, &mut m.journal, IDENTITY) {
            Ok(_) => Ok(()),
            Err(MoveError::UnexpectedLayout { found: Some(t), .. }) if t == s.to * S => {
                recover(&mut m.data, &mut m.journal, IDENTITY).map(|_| ())
            }
            Err(e) => Err(e),
        };
    run_campaign(
        || s.setup(),
        |m| s.operation(m),
        roll_back_or_finish,
        |m| s.verify(m),
        CampaignOptions {
            random_seeds: 2,
            crash_during_recovery: true,
        },
    )
    .assert_passed();
}

#[test]
fn neighbours_stay_in_the_chain_while_the_drive_is_out() {
    // Power cut right after the detach: the moved drive is out, the others are still listed.
    let s = scenario(1, 60, 6);
    let mut m = s.setup();
    // Journal write + flush, then the detach: three table writes (MBR and two EBRs) and a flush.
    m.data.disk.arm_crash(2 + 3 + 1);
    assert_eq!(s.operation(&mut m), Err(MoveError::Io(IoError::Crashed)));
    let mut m = m.power_cycle(LossModel::DropAll);
    let layout = m.data.layout().unwrap();
    let mut starts: Vec<u64> = layout
        .entries()
        .iter()
        .filter(|e| layout.is_logical(e))
        .map(|e| e.offset / S)
        .collect();
    starts.sort_unstable();
    assert_eq!(starts, vec![10, 82]);
    Scenario::recover(&mut m).unwrap();
    s.verify(&mut m).unwrap();
}
