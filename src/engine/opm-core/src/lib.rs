//! Engine core. `no_std + alloc` so the same code can run under Win32, WinPE or the NT native
//! subsystem. All disk access goes through [`BlockDevice`]; nothing here touches the OS.

#![no_std]

extern crate alloc;

pub mod device;

pub use device::{BlockDevice, IoError};
