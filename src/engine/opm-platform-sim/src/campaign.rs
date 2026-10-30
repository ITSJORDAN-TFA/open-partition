use std::fmt;

use opm_core::IoError;

use crate::disk::{LossModel, Power, SimDisk};

/// Everything a crash campaign can cut power to: one disk, or a machine with several disks on
/// one [`Power`] rail.
pub trait SimWorld: Sized {
    fn power(&self) -> &Power;

    /// The machine after power returns, with unflushed writes resolved by `model` (use
    /// [`LossModel::for_disk`] to vary it per disk) and every disk on one fresh rail.
    fn power_cycle(&self, model: LossModel) -> Self;
}

impl SimWorld for SimDisk {
    fn power(&self) -> &Power {
        SimDisk::power(self)
    }

    fn power_cycle(&self, model: LossModel) -> Self {
        SimDisk::power_cycle(self, model)
    }
}

/// Errors returned by operations under test. The runner needs to tell "power was cut" apart
/// from a genuine failure.
pub trait CrashAware: fmt::Display {
    fn is_crash(&self) -> bool;
}

impl CrashAware for IoError {
    fn is_crash(&self) -> bool {
        *self == IoError::Crashed
    }
}

impl CrashAware for opm_core::MoveError {
    fn is_crash(&self) -> bool {
        *self == opm_core::MoveError::Io(IoError::Crashed)
    }
}

#[derive(Debug, Clone, Copy)]
pub struct CampaignOptions {
    /// [`LossModel::Random`] seeds to try at each crash point, on top of `DropAll` and `KeepAll`.
    pub random_seeds: u64,
    /// Also cut power at every operation *of the recovery*, then recover again. Quadratic in
    /// run time, so keep scenarios small.
    pub crash_during_recovery: bool,
}

impl Default for CampaignOptions {
    fn default() -> Self {
        Self {
            random_seeds: 8,
            crash_during_recovery: false,
        }
    }
}

/// One crash point and loss model at which recovery did not produce a correct machine.
#[derive(Debug, Clone)]
pub struct CampaignFailure {
    /// Zero-based operation at which power was cut. `None` means the uncrashed baseline.
    pub crash_at: Option<u64>,
    /// For crash-during-recovery runs: the recovery operation at which power was cut again.
    pub recovery_crash_at: Option<u64>,
    pub model: LossModel,
    pub reason: String,
}

impl fmt::Display for CampaignFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match (self.crash_at, self.recovery_crash_at) {
            (None, _) => write!(f, "baseline run without a crash: {}", self.reason),
            (Some(op), None) => write!(f, "crash at op {op} ({:?}): {}", self.model, self.reason),
            (Some(op), Some(r)) => {
                write!(
                    f,
                    "crash at op {op}, then at recovery op {r} ({:?}): {}",
                    self.model, self.reason
                )
            }
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
        let shown: Vec<String> = self
            .failures
            .iter()
            .take(10)
            .map(ToString::to_string)
            .collect();
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
/// `recover` on the rebooted machine and check the result with `verify`.
///
/// * `setup` must return the same starting machine every time.
/// * `operation` must be deterministic, so crash point N means the same thing on every run.
/// * `recover` is what the service or boot runner does on start-up. It must also be correct
///   when the crash happened before the operation wrote anything, or after it finished.
/// * `verify` is given the machine as it would look after *another* power loss with every
///   unflushed write dropped, so recovery is only correct if its own work was flushed.
pub fn run_campaign<W, E, Setup, Op, Recover, Verify>(
    setup: Setup,
    operation: Op,
    recover: Recover,
    verify: Verify,
    options: CampaignOptions,
) -> CampaignReport
where
    W: SimWorld,
    E: CrashAware,
    Setup: Fn() -> W,
    Op: Fn(&mut W) -> Result<(), E>,
    Recover: Fn(&mut W) -> Result<(), E>,
    Verify: Fn(&mut W) -> Result<(), String>,
{
    let mut report = CampaignReport::default();
    let fail = |crash_at, recovery_crash_at, model, reason| CampaignFailure {
        crash_at,
        recovery_crash_at,
        model,
        reason,
    };

    let mut baseline = setup();
    let outcome = operation(&mut baseline)
        .map_err(|e| format!("operation failed: {e}"))
        .and_then(|()| verify(&mut baseline.power_cycle(LossModel::DropAll)));
    report.total_ops = baseline.power().ops();
    report.runs = 1;
    if let Err(reason) = outcome {
        report
            .failures
            .push(fail(None, None, LossModel::DropAll, reason));
        return report;
    }

    for crash_at in 0..report.total_ops {
        let models = [LossModel::DropAll, LossModel::KeepAll].into_iter().chain(
            (0..options.random_seeds).map(|s| LossModel::Random {
                seed: crash_at << 16 | s,
            }),
        );
        for model in models {
            report.runs += 1;
            let rebooted = match crash(&setup, &operation, crash_at, model) {
                Ok(world) => world,
                Err(reason) => {
                    report
                        .failures
                        .push(fail(Some(crash_at), None, model, reason));
                    continue;
                }
            };

            // Recovery on a copy, so the same rebooted state can be reused below.
            let mut world = rebooted.power_cycle(LossModel::KeepAll);
            if let Err(reason) = recover_and_verify(&recover, &verify, &mut world) {
                report
                    .failures
                    .push(fail(Some(crash_at), None, model, reason));
                continue;
            }

            if options.crash_during_recovery {
                let recovery_ops = world.power().ops();
                for recovery_crash_at in 0..recovery_ops {
                    for second in [
                        LossModel::DropAll,
                        LossModel::Random {
                            seed: recovery_crash_at,
                        },
                    ] {
                        report.runs += 1;
                        let world = rebooted.power_cycle(LossModel::KeepAll);
                        world.power().arm_crash(recovery_crash_at);
                        let mut world = world;
                        let result = match recover(&mut world) {
                            Err(e) if e.is_crash() => {
                                let mut again = world.power_cycle(second);
                                recover_and_verify(&recover, &verify, &mut again)
                            }
                            Err(e) => Err(format!("recovery failed with {e} instead of crashing")),
                            Ok(()) => Err("recovery finished without reaching the crash point; it is not deterministic".into()),
                        };
                        if let Err(reason) = result {
                            report.failures.push(fail(
                                Some(crash_at),
                                Some(recovery_crash_at),
                                second,
                                reason,
                            ));
                        }
                    }
                }
            }
        }
    }
    report
}

fn crash<W, E, Setup, Op>(
    setup: &Setup,
    operation: &Op,
    crash_at: u64,
    model: LossModel,
) -> Result<W, String>
where
    W: SimWorld,
    E: CrashAware,
    Setup: Fn() -> W,
    Op: Fn(&mut W) -> Result<(), E>,
{
    let mut world = setup();
    world.power().arm_crash(crash_at);
    match operation(&mut world) {
        Err(e) if e.is_crash() => Ok(world.power_cycle(model)),
        Err(e) => Err(format!("operation failed with {e} instead of crashing")),
        Ok(()) => Err(
            "operation finished without reaching the crash point; it is not deterministic".into(),
        ),
    }
}

fn recover_and_verify<W, E, Recover, Verify>(
    recover: &Recover,
    verify: &Verify,
    world: &mut W,
) -> Result<(), String>
where
    W: SimWorld,
    E: CrashAware,
    Recover: Fn(&mut W) -> Result<(), E>,
    Verify: Fn(&mut W) -> Result<(), String>,
{
    recover(world).map_err(|e| format!("recovery failed: {e}"))?;
    verify(&mut world.power_cycle(LossModel::DropAll))
}
