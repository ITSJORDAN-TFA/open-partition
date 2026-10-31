//! Win32 platform layer: raw disk I/O, volume lock and dismount, and partition table updates
//! through `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`.
//!
//! `unsafe` is denied crate-wide; each FFI call site opts in with `#[allow(unsafe_code)]` and a
//! `// SAFETY:` comment, so every one is visible in review.
//!
//! **Write guard.** [`PhysicalDisk`] refuses to open a disk for writing unless Windows reports
//! it as a file-backed virtual disk (a mounted VHD/VHDX), or the machine is an Azure VM (from
//! SMBIOS) and the caller confirmed a snapshot of the disk ([`WriteAccess`]).

#![cfg(windows)]

pub mod copy_map;
mod digest;
mod disk;
mod error;
mod guard;
mod handle;
mod journal_file;
mod layout;
mod mounted_devices;
mod ntfs;
mod smbios;
mod volume;

pub use disk::PhysicalDisk;
pub use error::PlatformError;
pub use guard::{bus_type, WriteAccess, BUS_TYPE_FILE_BACKED_VIRTUAL};
pub use journal_file::FileJournal;
pub use layout::{
    is_extended_type, snapshot_records, Layout, LayoutEntry, LayoutStyle, SnapshotRecord,
};
pub use smbios::is_azure_vm;
pub use volume::{find_volumes_in, lock_volumes_in, LockedVolume};
