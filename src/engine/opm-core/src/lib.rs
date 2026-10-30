//! Engine core. `no_std + alloc` so the same code can run under Win32, WinPE or the NT native
//! subsystem. All disk access goes through [`BlockDevice`]; nothing here touches the OS.

#![no_std]

extern crate alloc;

pub mod crc32;
pub mod device;
pub mod journal;
pub mod mover;

pub use device::{BlockDevice, IoError};
pub use journal::{
    DeviceJournal, Direction, JournalStore, MoveRecord, PartitionEntry, PartitionKey, Phase,
    ENTRY_SIZE,
};
pub use mover::{
    recover, recover_with_progress, start_move, start_move_with_progress, DiskIdentity, MoveError,
    MoveJob, PartitionTable, Progress, RangeDigest, Recovery,
};
