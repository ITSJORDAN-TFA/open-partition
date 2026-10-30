//! The write guard: decide whether a disk may be opened for writing at all.

use opm_core::IoError;

use crate::handle::Handle;
use crate::PlatformError;

const IOCTL_STORAGE_QUERY_PROPERTY: u32 = 0x002D_1400;

/// `STORAGE_BUS_TYPE` value Windows reports for a mounted VHD/VHDX.
pub const BUS_TYPE_FILE_BACKED_VIRTUAL: u32 = 15;

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

/// Refuse anything but a file-backed virtual disk. See the crate docs.
pub fn require_virtual_disk(disk_number: u32) -> Result<(), PlatformError> {
    let bus = bus_type(disk_number)?;
    if bus == BUS_TYPE_FILE_BACKED_VIRTUAL {
        Ok(())
    } else {
        Err(PlatformError::Refused(format!(
            "disk {disk_number} is not a VHD/VHDX (bus type {bus}); this build only writes to virtual test disks"
        )))
    }
}
