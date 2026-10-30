//! Simulated block device with power-loss modelling, and the crash campaign runner.
//!
//! This is the fast layer of the test strategy (plan section 4): every engine operation is run
//! with a simulated power cut after its 1st, 2nd, ... Nth disk operation, the disk is "rebooted"
//! under several loss models, recovery runs, and the result is checked byte for byte.

#![forbid(unsafe_code)]

mod campaign;
mod disk;
mod rng;

pub use campaign::{
    run_campaign, CampaignFailure, CampaignOptions, CampaignReport, CrashAware, SimWorld,
};
pub use disk::{LossModel, Power, SimDisk};
pub use rng::XorShift64;
