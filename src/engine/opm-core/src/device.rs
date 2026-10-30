use core::fmt;

/// Errors a block device can report. Kept small and `Copy` so it works without an allocator.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IoError {
    /// Offset or length is not a multiple of the sector size.
    Misaligned,
    /// The request extends past the end of the device.
    OutOfRange,
    /// The device (or simulated machine) lost power. Nothing after this point may be trusted.
    Crashed,
    /// Any other OS-level failure, carrying the raw platform status code.
    Os(i32),
}

impl fmt::Display for IoError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            IoError::Misaligned => f.write_str("request is not sector aligned"),
            IoError::OutOfRange => f.write_str("request is beyond the end of the device"),
            IoError::Crashed => f.write_str("device lost power"),
            IoError::Os(code) => write!(f, "OS error 0x{code:08x}"),
        }
    }
}

/// A raw, sector-addressed disk.
///
/// Durability contract: data passed to [`write_at`](Self::write_at) is only guaranteed to
/// survive a power loss once a later [`flush`](Self::flush) has returned `Ok`. Before that,
/// any subset of unflushed writes may be lost, reordered, or torn at sector granularity.
/// The simulated device in `opm-platform-sim` enforces exactly this contract.
pub trait BlockDevice {
    /// Logical sector size in bytes (512 or 4096).
    fn sector_size(&self) -> u32;

    /// Total device size in bytes. Always a multiple of [`sector_size`](Self::sector_size).
    fn size_bytes(&self) -> u64;

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<(), IoError>;

    fn write_at(&mut self, offset: u64, data: &[u8]) -> Result<(), IoError>;

    /// Make every completed write durable.
    fn flush(&mut self) -> Result<(), IoError>;

    /// Validate alignment and bounds for a request. Implementations should call this first.
    fn check_request(&self, offset: u64, len: usize) -> Result<(), IoError> {
        let sector = u64::from(self.sector_size());
        let len = len as u64;
        if offset % sector != 0 || len % sector != 0 {
            return Err(IoError::Misaligned);
        }
        match offset.checked_add(len) {
            Some(end) if end <= self.size_bytes() => Ok(()),
            _ => Err(IoError::OutOfRange),
        }
    }
}
