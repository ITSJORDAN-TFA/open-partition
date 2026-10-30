//! Win32 platform layer. Empty until the engine track: Phase 3 adds a `PhysicalDisk`
//! implementing [`opm_core::BlockDevice`] over `\\.\PhysicalDriveN`, opened with
//! `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH`, plus volume lock/dismount and the
//! `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` wrapper.
//!
//! `unsafe` is denied crate-wide; each FFI call site opts in with `#[allow(unsafe_code)]` and a
//! `// SAFETY:` comment, so every one is visible in review.

#![cfg(windows)]
