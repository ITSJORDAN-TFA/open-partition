//! The write guard: decide whether a disk may be opened for writing at all.
//!
//! Test virtual disks (mounted VHD/VHDX) can always be written. A real disk can only be written
//! on an Azure VM (detected from SMBIOS here, not trusted from the caller), and only when the
//! caller confirms a snapshot of the disk exists. Everything else is refused.

use opm_core::IoError;

use crate::handle::Handle;
use crate::smbios::is_azure_vm;
use crate::PlatformError;

const IOCTL_STORAGE_QUERY_PROPERTY: u32 = 0x002D_1400;

/// `STORAGE_BUS_TYPE` value Windows reports for a mounted VHD/VHDX.
pub const BUS_TYPE_FILE_BACKED_VIRTUAL: u32 = 15;

/// What the caller allows beyond test virtual disks.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WriteAccess {
    /// Only file-backed virtual disks.
    VirtualOnly,
    /// Also real disks on an Azure VM. The string names the snapshot the user says they took.
    AzureWithSnapshot(String),
}

/// Bus type of `\\.\PhysicalDriveN`, from `STORAGE_DEVICE_DESCRIPTOR`. Query-only access.
pub fn bus_type(disk_number: u32) -> Result<u32, IoError> {
    let handle = Handle::open(&format!(r"\\.\PhysicalDrive{disk_number}"), 0, 0)?;
    // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0) }
    let query = [0u8; 12];
    let descriptor = handle.ioctl_vec(IOCTL_STORAGE_QUERY_PROPERTY, &query, 1024)?;
    // STORAGE_DEVICE_DESCRIPTOR.BusType is the u32 at byte 28.
    descriptor
        .get(28..32)
        .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
        .ok_or(IoError::Os(13))
}

/// Refuse the disk unless `access` allows writing to it. See the module docs.
pub fn require_writable(disk_number: u32, access: &WriteAccess) -> Result<(), PlatformError> {
    let bus = bus_type(disk_number)?;
    if bus == BUS_TYPE_FILE_BACKED_VIRTUAL {
        return Ok(());
    }
    match access {
        WriteAccess::VirtualOnly => Err(PlatformError::Refused(format!(
            "disk {disk_number} is not a VHD/VHDX (bus type {bus}); real disks can only be changed on an Azure VM after confirming a snapshot"
        ))),
        WriteAccess::AzureWithSnapshot(snapshot) if snapshot.trim().is_empty() => Err(
            PlatformError::Refused("name the snapshot you took of this disk".into()),
        ),
        WriteAccess::AzureWithSnapshot(_) if !is_azure_vm() => Err(PlatformError::Refused(format!(
            "disk {disk_number} is a real disk and this machine is not an Azure VM; this version only changes real disks on Azure"
        ))),
        WriteAccess::AzureWithSnapshot(_) => Ok(()),
    }
}
