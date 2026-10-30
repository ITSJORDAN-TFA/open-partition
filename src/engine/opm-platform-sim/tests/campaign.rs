//! Self-tests for the campaign runner: it must pass a correctly ordered commit protocol and
//! catch the classic bug of writing the commit marker before the data is durable.

use opm_core::{BlockDevice, IoError};
use opm_platform_sim::{run_campaign, SimDisk};

const SECTOR: u32 = 512;
const DATA_OFFSET: u64 = 0;
const DATA_LEN: usize = 16 * SECTOR as usize;
const MARKER_OFFSET: u64 = 32 * SECTOR as u64;
const DONE: u8 = 0xD0;

fn setup() -> SimDisk {
    SimDisk::new(SECTOR, 64 * u64::from(SECTOR))
}

fn payload() -> Vec<u8> {
    (0..DATA_LEN).map(|i| (i % 251) as u8).collect()
}

fn marker() -> Vec<u8> {
    vec![DONE; SECTOR as usize]
}

fn safe_commit(disk: &mut SimDisk) -> Result<(), IoError> {
    disk.write_at(DATA_OFFSET, &payload())?;
    disk.flush()?;
    disk.write_at(MARKER_OFFSET, &marker())?;
    disk.flush()
}

fn unsafe_commit(disk: &mut SimDisk) -> Result<(), IoError> {
    disk.write_at(DATA_OFFSET, &payload())?;
    disk.write_at(MARKER_OFFSET, &marker())?;
    disk.flush()
}

fn recover(disk: &mut SimDisk) -> Result<(), IoError> {
    let mut sector = vec![0; SECTOR as usize];
    disk.read_at(MARKER_OFFSET, &mut sector)?;
    if sector[0] == DONE {
        Ok(())
    } else {
        safe_commit(disk)
    }
}

fn verify(disk: &SimDisk) -> Result<(), String> {
    let contents = disk.contents();
    let data = &contents[DATA_OFFSET as usize..DATA_OFFSET as usize + DATA_LEN];
    if data != payload().as_slice() {
        return Err("payload does not match".into());
    }
    if contents[MARKER_OFFSET as usize] != DONE {
        return Err("commit marker missing".into());
    }
    Ok(())
}

#[test]
fn correctly_ordered_commit_survives_every_crash() {
    let report = run_campaign(setup, safe_commit, recover, verify, 16);
    assert_eq!(report.total_ops, 4);
    assert_eq!(report.runs, 1 + 4 * (2 + 16));
    report.assert_passed();
}

#[test]
fn marker_before_flush_is_caught() {
    let report = run_campaign(setup, unsafe_commit, recover, verify, 16);
    assert!(!report.passed(), "campaign failed to detect an unsafe commit order");
    // Only crashes after the marker has been issued, but before the flush, can expose the bug.
    assert!(
        report.failures.iter().all(|f| matches!(f.crash_at, Some(1 | 2))),
        "{:?}",
        report.failures
    );
}

#[test]
fn recovery_that_forgets_to_flush_is_caught() {
    let lazy_recover = |disk: &mut SimDisk| -> Result<(), IoError> {
        disk.write_at(DATA_OFFSET, &payload())?;
        disk.write_at(MARKER_OFFSET, &marker())
    };
    let report = run_campaign(setup, safe_commit, lazy_recover, verify, 4);
    assert!(!report.passed());
}
