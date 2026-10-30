//! Win32 platform layer: raw disk I/O, volume lock and dismount, and partition table updates
//! through `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`.
//!
//! `unsafe` is denied crate-wide; each FFI call site opts in with `#[allow(unsafe_code)]` and a
//! `// SAFETY:` comment, so every one is visible in review.
//!
//! **Write guard.** Until the engine has passed VM fault-injection testing, [`PhysicalDisk`]
//! refuses to open any disk for writing unless Windows reports it as a file-backed virtual
//! disk (a mounted VHD/VHDX). Real disks cannot be written by this build at all.

#![cfg(windows)]

mod disk;
mod error;
mod guard;
mod handle;
mod journal_file;
mod layout;
mod volume;

pub use disk::PhysicalDisk;
pub use error::PlatformError;
pub use guard::{bus_type, BUS_TYPE_FILE_BACKED_VIRTUAL};
pub use journal_file::FileJournal;
pub use layout::{Layout, LayoutEntry, LayoutStyle};
pub use volume::{find_volume, LockedVolume};
