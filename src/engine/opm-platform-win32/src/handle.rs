use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;

use opm_core::IoError;
use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FlushFileBuffers, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows_sys::Win32::System::IO::DeviceIoControl;

pub const GENERIC_READ: u32 = 0x8000_0000;
pub const GENERIC_WRITE: u32 = 0x4000_0000;

const ERROR_INSUFFICIENT_BUFFER: u32 = 122;
const ERROR_MORE_DATA: u32 = 234;

pub fn last_error() -> IoError {
    #[allow(unsafe_code)]
    // SAFETY: GetLastError has no preconditions.
    let code = unsafe { GetLastError() };
    IoError::Os(code as i32)
}

pub fn wide(s: &str) -> Vec<u16> {
    std::ffi::OsStr::new(s)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

/// An owned Win32 handle to a device, closed on drop.
#[derive(Debug)]
pub struct Handle(HANDLE);

impl Handle {
    /// Open a device. Always shares read and write so other tools keep working.
    pub fn open(path: &str, access: u32, flags: u32) -> Result<Self, IoError> {
        let name = wide(path);
        #[allow(unsafe_code)]
        // SAFETY: `name` is a NUL-terminated UTF-16 string that outlives the call; the
        // security attributes and template handle are optional and passed as null.
        let handle = unsafe {
            CreateFileW(
                name.as_ptr(),
                access,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                std::ptr::null(),
                OPEN_EXISTING,
                flags,
                std::ptr::null_mut(),
            )
        };
        if handle == INVALID_HANDLE_VALUE {
            return Err(last_error());
        }
        Ok(Self(handle))
    }

    pub fn raw(&self) -> HANDLE {
        self.0
    }

    /// Send an IOCTL. Returns the number of bytes written to `output`.
    pub fn ioctl(&self, code: u32, input: &[u8], output: &mut [u8]) -> Result<usize, IoError> {
        let mut returned = 0u32;
        #[allow(unsafe_code)]
        // SAFETY: both buffers are valid for the lengths passed; the call is synchronous
        // (no OVERLAPPED), so they only need to live for its duration.
        let ok = unsafe {
            DeviceIoControl(
                self.0,
                code,
                if input.is_empty() {
                    std::ptr::null()
                } else {
                    input.as_ptr().cast::<c_void>()
                },
                input.len() as u32,
                if output.is_empty() {
                    std::ptr::null_mut()
                } else {
                    output.as_mut_ptr().cast::<c_void>()
                },
                output.len() as u32,
                &mut returned,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(last_error());
        }
        Ok(returned as usize)
    }

    /// Send an IOCTL whose output size isn't known in advance, growing the buffer as needed.
    pub fn ioctl_vec(&self, code: u32, input: &[u8], initial: usize) -> Result<Vec<u8>, IoError> {
        let mut size = initial.max(64);
        loop {
            let mut buffer = vec![0u8; size];
            match self.ioctl(code, input, &mut buffer) {
                Ok(n) => {
                    buffer.truncate(n);
                    return Ok(buffer);
                }
                Err(IoError::Os(e))
                    if (e as u32 == ERROR_INSUFFICIENT_BUFFER || e as u32 == ERROR_MORE_DATA)
                        && size < 16 << 20 =>
                {
                    size *= 2;
                }
                Err(e) => return Err(e),
            }
        }
    }

    pub fn flush(&self) -> Result<(), IoError> {
        #[allow(unsafe_code)]
        // SAFETY: the handle is open for the lifetime of `self`.
        let ok = unsafe { FlushFileBuffers(self.0) };
        if ok == 0 {
            return Err(last_error());
        }
        Ok(())
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        #[allow(unsafe_code)]
        // SAFETY: we own the handle and close it exactly once.
        unsafe {
            CloseHandle(self.0);
        }
    }
}
