use std::fmt;

use opm_core::IoError;

use crate::disk::{LossModel, SimDisk};

/// One crash point and loss model at which recovery did not produce a correct disk.
#[derive(Debug, Clone)]
pub struct CampaignFailure {
    /// Zero-based disk operation at which power was cut. `None` means the uncrashed baseline.
    pub crash_at: Option<u64>,
    pub model: LossModel,
    pub reason: String,
}

impl fmt::Display for CampaignFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.crash_at {
            Some(op) => write!(f, "crash at op {op} ({:?}): {}", self.model, self.reason),
            None => write!(f, "baseline run without a crash: {}", self.reason),
        }
    }
}

#[derive(Debug, Clone, Default)]
pub struct CampaignReport {
    /// Disk operations (writes + flushes) the operation issues when nothing goes wrong.
    pub total_ops: u64,
    /// Crash/recover/verify cycles executed.
    pub runs: u64,
    pub failures: Vec<CampaignFailure>,
}

impl CampaignReport {
    pub fn passed(&self) -> bool {
        self.failures.is_empty()
    }

    /// Panic with a readable summary if any run failed. Intended for tests.
    #[track_caller]
    pub fn assert_passed(&self) {
        if self.passed() {
            return;
        }
        let shown: Vec<String> = self.failures.iter().take(10).map(ToString::to_string).collect();
        panic!(
            "{} of {} crash runs failed (operation issues {} disk ops). First failures:\n  {}",
            self.failures.len(),
            self.runs,
            self.total_ops,
            shown.join("\n  ")
        );
    }
}

/// Cut power at every disk operation of `operation`, under several loss models, then run
/// `recover` on the rebooted disk and check the result with `verify`.
///
/// * `setup` must return the same starting disk every time.
/// * `operation` must be deterministic, so crash point N means the same thing on every run.
/// * `recover` is what the service or boot runner does on start-up. It must also be correct
///   when the crash happened before the operation wrote anything, or after it finished.
/// * `verify` is given the disk as it would look after *another* power loss with every
///   unflushed write dropped, so recovery is only correct if its own work was flushed.
///
/// Each crash point runs under [`LossModel::DropAll`], [`LossModel::KeepAll`] and
/// `random_seeds` different [`LossModel::Random`] seeds.
pub fn run_campaign<Setup, Op, Recover, Verify>(
    setup: Setup,
    operation: Op,
    recover: Recover,
    verify: Verify,
    random_seeds: u64,
) -> CampaignReport
where
    Setup: Fn() -> SimDisk,
    Op: Fn(&mut SimDisk) -> Result<(), IoError>,
    Recover: Fn(&mut SimDisk) -> Result<(), IoError>,
    Verify: Fn(&SimDisk) -> Result<(), String>,
{
    let mut report = CampaignReport::default();

    let mut baseline = setup();
    let outcome = operation(&mut baseline)
        .map_err(|e| format!("operation failed: {e}"))
        .and_then(|()| verify(&baseline.power_cycle(LossModel::DropAll)));
    report.total_ops = baseline.ops();
    report.runs = 1;
    if let Err(reason) = outcome {
        report.failures.push(CampaignFailure { crash_at: None, model: LossModel::DropAll, reason });
        return report;
    }

    for crash_at in 0..report.total_ops {
        let models = [LossModel::DropAll, LossModel::KeepAll]
            .into_iter()
            .chain((0..random_seeds).map(|s| LossModel::Random { seed: crash_at << 16 | s }));
        for model in models {
            report.runs += 1;
            if let Err(reason) = crash_and_recover(&setup, &operation, &recover, &verify, crash_at, model)
            {
                report.failures.push(CampaignFailure { crash_at: Some(crash_at), model, reason });
            }
        }
    }
    report
}

fn crash_and_recover<Setup, Op, Recover, Verify>(
    setup: &Setup,
    operation: &Op,
    recover: &Recover,
    verify: &Verify,
    crash_at: u64,
    model: LossModel,
) -> Result<(), String>
where
    Setup: Fn() -> SimDisk,
    Op: Fn(&mut SimDisk) -> Result<(), IoError>,
    Recover: Fn(&mut SimDisk) -> Result<(), IoError>,
    Verify: Fn(&SimDisk) -> Result<(), String>,
{
    let mut disk = setup();
    disk.arm_crash(crash_at);
    match operation(&mut disk) {
        Err(IoError::Crashed) => {}
        Err(e) => return Err(format!("operation failed with {e} instead of crashing")),
        Ok(()) => return Err("operation finished without reaching the crash point; it is not deterministic".into()),
    }
    let mut rebooted = disk.power_cycle(model);
    recover(&mut rebooted).map_err(|e| format!("recovery failed: {e}"))?;
    verify(&rebooted.power_cycle(LossModel::DropAll))
}
