//! Crash campaigns for the journaled partition mover (plan Phase 3 acceptance, sim layer).
//!
//! The simulated machine has a data disk, whose first sector holds a one-entry partition
//! table, and a separate journal disk (plan gap G5) on the same power rail.

use opm_core::journal::load_newest;
use opm_core::mover::{default_chunk_size, validate};
use opm_core::{
    recover, start_move, BlockDevice, DeviceJournal, DiskIdentity, IoError, MoveError, MoveJob,
    PartitionEntry, PartitionKey, PartitionTable, Phase, RangeDigest, Recovery, ENTRY_SIZE,
};
use opm_platform_sim::{run_campaign, CampaignOptions, LossModel, Power, SimDisk, SimWorld};

const KEY: PartitionKey = PartitionKey(*b"partition-key-01");
const IDENTITY: DiskIdentity = DiskIdentity {
    id: *b"disk-identity-01",
};
const DATA_TYPE: [u8; 16] = *b"basic-data-type!";

/// A simulated disk whose sector 0 is a one-entry partition table: 16-byte key, the start as a
/// little-endian u64, the 16-byte type, then the length as a little-endian u64. An all-zero key
/// means the table is empty. One sector, so updating it is atomic.
#[derive(Debug)]
struct TableDisk(SimDisk);

impl BlockDevice for TableDisk {
    fn sector_size(&self) -> u32 {
        self.0.sector_size()
    }
    fn size_bytes(&self) -> u64 {
        self.0.size_bytes()
    }
    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<(), IoError> {
        self.0.read_at(offset, buf)
    }
    fn write_at(&mut self, offset: u64, data: &[u8]) -> Result<(), IoError> {
        self.0.write_at(offset, data)
    }
    fn flush(&mut self) -> Result<(), IoError> {
        self.0.flush()
    }
}

/// One table entry as stored in sector 0.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Row {
    key: [u8; 16],
    start: u64,
    kind: [u8; 16],
    length: u64,
}

impl TableDisk {
    fn row(&mut self) -> Result<Option<Row>, IoError> {
        let mut sector = vec![0; self.sector_size() as usize];
        self.read_at(0, &mut sector)?;
        let key: [u8; 16] = sector[..16].try_into().unwrap();
        Ok((key != [0; 16]).then(|| Row {
            key,
            start: u64::from_le_bytes(sector[16..24].try_into().unwrap()),
            kind: sector[24..40].try_into().unwrap(),
            length: u64::from_le_bytes(sector[40..48].try_into().unwrap()),
        }))
    }

    fn write_row(&mut self, row: Option<Row>) -> Result<(), IoError> {
        let mut sector = vec![0; self.sector_size() as usize];
        if let Some(r) = row {
            sector[..16].copy_from_slice(&r.key);
            sector[16..24].copy_from_slice(&r.start.to_le_bytes());
            sector[24..40].copy_from_slice(&r.kind);
            sector[40..48].copy_from_slice(&r.length.to_le_bytes());
        }
        self.write_at(0, &sector)?;
        self.flush()
    }

    fn row_of(&mut self, partition: PartitionKey) -> Result<Option<Row>, IoError> {
        Ok(self.row()?.filter(|r| r.key == partition.0))
    }
}

impl PartitionTable for TableDisk {
    fn partition_start(&mut self, partition: PartitionKey) -> Result<Option<u64>, IoError> {
        Ok(self.row_of(partition)?.map(|r| r.start))
    }

    fn entry(&mut self, partition: PartitionKey) -> Result<PartitionEntry, IoError> {
        let row = self.row_of(partition)?.ok_or(IoError::Os(2))?;
        let mut entry = [0u8; ENTRY_SIZE];
        entry[..16].copy_from_slice(&row.kind);
        entry[16..24].copy_from_slice(&row.length.to_le_bytes());
        Ok(PartitionEntry(entry))
    }

    fn detach(&mut self, partition: PartitionKey) -> Result<(), IoError> {
        match self.row()? {
            Some(r) if r.key == partition.0 => self.write_row(None),
            _ => Ok(()),
        }
    }

    fn attach(
        &mut self,
        partition: PartitionKey,
        entry: &PartitionEntry,
        start: u64,
    ) -> Result<(), IoError> {
        if self.row()?.is_some() {
            return Err(IoError::Os(183)); // the one slot is taken
        }
        self.write_row(Some(Row {
            key: partition.0,
            start,
            kind: entry.0[..16].try_into().unwrap(),
            length: u64::from_le_bytes(entry.0[16..24].try_into().unwrap()),
        }))
    }

    fn range_is_free(&mut self, start: u64, length: u64) -> Result<bool, IoError> {
        Ok(self
            .row()?
            .is_none_or(|r| r.start + r.length <= start || start + length <= r.start))
    }
}

impl RangeDigest for TableDisk {
    fn range_digest(&mut self, offset: u64, length: u64) -> Result<[u8; 32], IoError> {
        use sha2::{Digest, Sha256};
        let mut buf = vec![0; length as usize];
        self.read_at(offset, &mut buf)?;
        Ok(Sha256::digest(&buf).into())
    }
}

#[derive(Debug)]
struct Machine {
    data: TableDisk,
    journal: DeviceJournal<SimDisk>,
}

impl SimWorld for Machine {
    fn power(&self) -> &Power {
        self.data.0.power()
    }

    fn power_cycle(&self, model: LossModel) -> Self {
        let power = Power::new();
        Machine {
            data: TableDisk(self.data.0.power_cycle_on(&power, model.for_disk(0))),
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

/// Sizes are in sectors.
#[derive(Debug, Clone, Copy)]
struct Scenario {
    sector: u32,
    disk: u64,
    source: u64,
    target: u64,
    length: u64,
    chunk: u64,
}

impl Scenario {
    fn bytes(&self, sectors: u64) -> u64 {
        sectors * u64::from(self.sector)
    }

    fn job(&self) -> MoveJob {
        MoveJob {
            operation_id: *b"operation-id-001",
            partition: KEY,
            source_offset: self.bytes(self.source),
            target_offset: self.bytes(self.target),
            length: self.bytes(self.length),
            chunk_size: self.bytes(self.chunk),
            verify: true,
        }
    }

    /// Partition contents: depends only on the position inside the partition, so a byte in
    /// the wrong place or a stale byte from the old location is detected.
    fn partition_byte(i: usize) -> u8 {
        (i.wrapping_mul(31) ^ (i >> 9).wrapping_mul(17) ^ 0x5A) as u8
    }

    fn setup(&self) -> Machine {
        let size = self.bytes(self.disk) as usize;
        // Background bytes differ from every partition byte pattern position.
        let mut image: Vec<u8> = (0..size).map(|i| (i % 7) as u8 | 0x80).collect();
        let start = self.bytes(self.source) as usize;
        for i in 0..self.bytes(self.length) as usize {
            image[start + i] = Self::partition_byte(i);
        }
        let sector = self.sector as usize;
        image[..sector].fill(0);
        image[..16].copy_from_slice(&KEY.0);
        image[16..24].copy_from_slice(&self.bytes(self.source).to_le_bytes());
        image[24..40].copy_from_slice(&DATA_TYPE);
        image[40..48].copy_from_slice(&self.bytes(self.length).to_le_bytes());

        let power = Power::new();
        let data = TableDisk(SimDisk::from_image_on_rail(&power, self.sector, image));
        let journal =
            DeviceJournal::new(SimDisk::on_rail(&power, self.sector, 4 * 4096), 0).unwrap();
        Machine { data, journal }
    }

    fn operation(&self, m: &mut Machine) -> Result<(), MoveError> {
        start_move(&mut m.data, &mut m.journal, IDENTITY, &self.job(), &[]).map(|_| ())
    }

    fn recover(m: &mut Machine) -> Result<(), MoveError> {
        recover(&mut m.data, &mut m.journal, IDENTITY).map(|_| ())
    }

    /// The move either completed fully or never started; nothing in between is acceptable.
    fn verify(&self, m: &mut Machine) -> Result<(), String> {
        let row = m.data.row_of(KEY).map_err(|e| e.to_string())?;
        let Some(row) = row else {
            return Err("partition left out of the table after recovery".into());
        };
        if row.kind != DATA_TYPE || row.length != self.bytes(self.length) {
            return Err(format!("entry not restored exactly: {row:?}"));
        }
        let start = Some(row.start);
        let journal = load_newest(&mut m.journal).map_err(|e| format!("{e:?}"))?;
        let expected_at = |m: &mut Machine, offset: u64| -> Result<(), String> {
            let mut buf = vec![0; self.bytes(self.length) as usize];
            m.data
                .read_at(offset, &mut buf)
                .map_err(|e| e.to_string())?;
            match buf
                .iter()
                .enumerate()
                .find(|(i, b)| **b != Self::partition_byte(*i))
            {
                None => Ok(()),
                Some((i, _)) => Err(format!("partition data wrong at partition byte {i}")),
            }
        };
        match start {
            Some(s) if s == self.bytes(self.target) => {
                if journal.map(|r| r.phase) != Some(Phase::Done) {
                    return Err(format!("table updated but journal is {journal:?}"));
                }
                expected_at(m, s)
            }
            Some(s) if s == self.bytes(self.source) => {
                if journal.is_some_and(|r| r.phase != Phase::Done) {
                    return Err(format!("move not finished: journal is {journal:?}"));
                }
                expected_at(m, s)
                    .map_err(|e| format!("move never started, but source damaged: {e}"))
            }
            other => Err(format!("partition table entry is {other:?}")),
        }
    }

    fn campaign(self, options: CampaignOptions) {
        let report = run_campaign(
            || self.setup(),
            |m| self.operation(m),
            Self::recover,
            |m| self.verify(m),
            options,
        );
        report.assert_passed();
        assert!(report.total_ops > 0);
    }
}

fn options(random_seeds: u64) -> CampaignOptions {
    CampaignOptions {
        random_seeds,
        crash_during_recovery: false,
    }
}

#[test]
fn move_right_overlapping_chunk_equals_shift() {
    Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    }
    .campaign(options(8));
}

#[test]
fn move_right_overlapping_chunk_smaller_than_shift() {
    Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 14,
        length: 16,
        chunk: 2,
    }
    .campaign(options(8));
}

#[test]
fn move_left_overlapping() {
    Scenario {
        sector: 512,
        disk: 64,
        source: 12,
        target: 8,
        length: 16,
        chunk: 4,
    }
    .campaign(options(8));
}

#[test]
fn move_with_partial_last_chunk() {
    Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 11,
        length: 10,
        chunk: 3,
    }
    .campaign(options(8));
    Scenario {
        sector: 512,
        disk: 64,
        source: 11,
        target: 8,
        length: 10,
        chunk: 3,
    }
    .campaign(options(8));
}

#[test]
fn move_far_without_overlap() {
    Scenario {
        sector: 512,
        disk: 128,
        source: 8,
        target: 80,
        length: 24,
        chunk: 8,
    }
    .campaign(options(8));
}

#[test]
fn move_on_4k_sector_disk() {
    Scenario {
        sector: 4096,
        disk: 32,
        source: 4,
        target: 7,
        length: 8,
        chunk: 3,
    }
    .campaign(options(8));
}

#[test]
fn crashes_during_recovery_are_survived() {
    Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 11,
        length: 7,
        chunk: 3,
    }
    .campaign(CampaignOptions {
        random_seeds: 2,
        crash_during_recovery: true,
    });
    Scenario {
        sector: 512,
        disk: 64,
        source: 11,
        target: 8,
        length: 7,
        chunk: 3,
    }
    .campaign(CampaignOptions {
        random_seeds: 2,
        crash_during_recovery: true,
    });
}

#[test]
fn completed_move_reports_done_and_recovery_has_nothing_to_do() {
    let s = Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    };
    let mut m = s.setup();
    let record = start_move(&mut m.data, &mut m.journal, IDENTITY, &s.job(), &[]).unwrap();
    assert_eq!(record.phase, Phase::Done);
    assert_eq!(
        recover(&mut m.data, &mut m.journal, IDENTITY),
        Ok(Recovery::NothingToDo)
    );
}

#[test]
fn second_move_waits_for_unfinished_first_move() {
    let s = Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    };
    let m = s.setup();
    m.data.0.arm_crash(5);
    let mut m = m;
    assert_eq!(s.operation(&mut m), Err(MoveError::Io(IoError::Crashed)));
    let mut m = m.power_cycle(LossModel::DropAll);
    assert_eq!(s.operation(&mut m), Err(MoveError::PendingOperation));
    assert!(matches!(
        recover(&mut m.data, &mut m.journal, IDENTITY),
        Ok(Recovery::Completed(_))
    ));
}

#[test]
fn journal_from_another_disk_is_refused() {
    let s = Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    };
    let m = s.setup();
    m.data.0.arm_crash(5);
    let mut m = m;
    let _ = s.operation(&mut m);
    let mut m = m.power_cycle(LossModel::DropAll);
    let other = DiskIdentity {
        id: *b"some-other-disk!",
    };
    assert_eq!(
        recover(&mut m.data, &mut m.journal, other),
        Err(MoveError::WrongDisk)
    );
}

#[test]
fn move_refused_when_partition_is_not_where_the_job_says() {
    let s = Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    };
    let mut m = s.setup();
    let wrong = MoveJob {
        source_offset: s.bytes(9),
        target_offset: s.bytes(13),
        ..s.job()
    };
    assert!(matches!(
        start_move(&mut m.data, &mut m.journal, IDENTITY, &wrong, &[]),
        Err(MoveError::UnexpectedLayout { .. })
    ));
    assert_eq!(
        m.data.0.ops(),
        0,
        "nothing may be written when a move is refused"
    );
}

#[test]
fn default_chunk_size_is_always_valid() {
    for (source, target) in [(8u64, 12u64), (12, 8), (8, 9), (8, 60)] {
        let s = Scenario {
            sector: 512,
            disk: 128,
            source,
            target,
            length: 16,
            chunk: 0,
        };
        let chunk = default_chunk_size(s.bytes(source), s.bytes(target), 512);
        let job = MoveJob {
            chunk_size: chunk,
            ..s.job()
        };
        assert!(
            validate(&job, &s.setup().data, &[]).is_ok(),
            "{source}->{target} chunk {chunk}"
        );
    }
}

/// Writes and flushes on the power rail: journal write+flush, detach write+flush. Each chunk
/// then costs 4 more (data write+flush, checkpoint write+flush).
const OPS_BEFORE_FIRST_CHUNK: u64 = 4;

#[test]
fn partition_is_out_of_the_table_while_data_is_in_flux() {
    // Crash right after the first chunk is written: the table must not list the partition.
    let s = Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    };
    let m = s.setup();
    m.data.0.arm_crash(OPS_BEFORE_FIRST_CHUNK);
    let mut m = m;
    assert_eq!(s.operation(&mut m), Err(MoveError::Io(IoError::Crashed)));
    let mut m = m.power_cycle(LossModel::DropAll);
    assert_eq!(
        m.data.partition_start(KEY).unwrap(),
        None,
        "entry must be removed before any data moves"
    );
    assert!(matches!(Scenario::recover(&mut m), Ok(())));
    s.verify(&mut m).unwrap();
}

#[test]
fn partition_created_over_the_data_stops_recovery() {
    let s = Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    };
    let m = s.setup();
    m.data.0.arm_crash(OPS_BEFORE_FIRST_CHUNK + 4);
    let mut m = m;
    assert_eq!(s.operation(&mut m), Err(MoveError::Io(IoError::Crashed)));
    let mut m = m.power_cycle(LossModel::KeepAll);
    // Someone uses Disk Management on the "free" space before recovery runs.
    let intruder = Row {
        key: *b"someone-elses-pt",
        start: s.bytes(20),
        kind: *b"other-data-type!",
        length: s.bytes(4),
    };
    m.data.write_row(Some(intruder)).unwrap();
    let ops = m.data.0.ops();
    assert_eq!(
        Scenario::recover(&mut m),
        Err(MoveError::RangeInUse {
            offset: s.bytes(s.source)
        })
    );
    assert_eq!(
        m.data.0.ops() - ops,
        0,
        "nothing may be written over another partition"
    );
    assert_eq!(m.data.row().unwrap(), Some(intruder));
}

#[test]
fn corrupted_copy_is_caught_before_the_partition_is_revealed() {
    let s = Scenario {
        sector: 512,
        disk: 64,
        source: 8,
        target: 12,
        length: 16,
        chunk: 4,
    };
    let m = s.setup();
    // Crash on the flush of the last checkpoint, so the copy is complete but the entry is
    // still out of the table.
    m.data.0.arm_crash(OPS_BEFORE_FIRST_CHUNK + 4 * 4 - 1);
    let mut m = m;
    assert_eq!(s.operation(&mut m), Err(MoveError::Io(IoError::Crashed)));
    let mut m = m.power_cycle(LossModel::KeepAll);
    // Bit rot in the copied data while the machine was off.
    let target = s.bytes(s.target);
    m.data.write_at(target, &vec![0xEE; 512]).unwrap();
    m.data.flush().unwrap();
    assert_eq!(
        Scenario::recover(&mut m),
        Err(MoveError::VerifyFailed { offset: target })
    );
    assert_eq!(
        m.data.partition_start(KEY).unwrap(),
        None,
        "a copy that failed verification must stay out of the table"
    );
}
