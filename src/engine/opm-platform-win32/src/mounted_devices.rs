//! Keeps drive letters and volume GUIDs on MBR partitions that move (plan gap G1).
//!
//! Windows identifies a basic MBR volume by 12 bytes: the disk signature and the partition's
//! starting offset. `HKLM\SYSTEM\MountedDevices` maps that ID to the volume's drive letter
//! (`\DosDevices\E:`) and GUID (`\??\Volume{...}`), so a moved partition would come back
//! with neither. While the moved partitions are out of the table, every value holding an old
//! ID is pointed at the new one; when the partitions arrive again, the mount manager finds
//! their letter and GUID as before. GPT volumes are identified by partition GUID and need none
//! of this.
//!
//! The rewrite is planned once and the plan is saved before any value changes, so a crash part
//! way through is replayed from the plan instead of re-matched against half-updated values
//! (where a new offset can equal another partition's old offset).

use opm_core::IoError;
use windows_sys::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_NO_MORE_ITEMS, ERROR_SUCCESS};
use windows_sys::Win32::System::Registry::{
    RegCloseKey, RegCreateKeyExW, RegDeleteValueW, RegEnumValueW, RegFlushKey, RegOpenKeyExW,
    RegQueryValueExW, RegSetValueExW, HKEY, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WRITE, REG_BINARY,
    REG_OPTION_NON_VOLATILE,
};

use crate::handle::wide;

const MOUNTED_DEVICES: &str = r"SYSTEM\MountedDevices";
const PLAN_KEY: &str = r"SOFTWARE\OpenPartition";
const PLAN_VALUE: &str = "PendingMountedDevices";
const MBR_ID_LEN: usize = 12;
const VOLUME_PREFIX: &str = r"\??\Volume";

/// Partitions of one MBR disk that move, as (old start, new start) in bytes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Remap {
    pub signature: [u8; 4],
    pub moves: Vec<(u64, u64)>,
}

impl Remap {
    fn header(&self) -> Vec<u8> {
        let mut out = self.signature.to_vec();
        out.extend_from_slice(&(self.moves.len() as u32).to_le_bytes());
        for (old, new) in &self.moves {
            out.extend_from_slice(&old.to_le_bytes());
            out.extend_from_slice(&new.to_le_bytes());
        }
        out
    }

    /// The volume name Windows computes for an MBR volume with no stored name:
    /// `\??\Volume{SSSSSSSS-0000-0000-OOOO-OOOOOOOOOOOO}`, the signature then the start's bytes.
    fn computed_volume_name(&self, offset: u64) -> String {
        let o = offset.to_le_bytes();
        format!(
            "{VOLUME_PREFIX}{{{:08x}-0000-0000-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}}}",
            u32::from_le_bytes(self.signature),
            o[0],
            o[1],
            o[2],
            o[3],
            o[4],
            o[5],
            o[6],
            o[7]
        )
    }

    fn id(&self, offset: u64) -> [u8; MBR_ID_LEN] {
        let mut id = [0u8; MBR_ID_LEN];
        id[..4].copy_from_slice(&self.signature);
        id[4..].copy_from_slice(&offset.to_le_bytes());
        id
    }
}

/// One value to set: its name and new data.
type Plan = Vec<(String, [u8; MBR_ID_LEN])>;

#[derive(Debug)]
struct Key(HKEY);

impl Key {
    fn open(path: &str, access: u32) -> Result<Self, IoError> {
        let name = wide(path);
        let mut key: HKEY = std::ptr::null_mut();
        #[allow(unsafe_code)]
        // SAFETY: `name` is NUL-terminated and outlives the call; `key` receives the handle.
        let status =
            unsafe { RegOpenKeyExW(HKEY_LOCAL_MACHINE, name.as_ptr(), 0, access, &mut key) };
        check(status)?;
        Ok(Self(key))
    }

    fn create(path: &str) -> Result<Self, IoError> {
        let name = wide(path);
        let mut key: HKEY = std::ptr::null_mut();
        #[allow(unsafe_code)]
        // SAFETY: as in `open`; class, security attributes and disposition are optional.
        let status = unsafe {
            RegCreateKeyExW(
                HKEY_LOCAL_MACHINE,
                name.as_ptr(),
                0,
                std::ptr::null(),
                REG_OPTION_NON_VOLATILE,
                KEY_READ | KEY_WRITE,
                std::ptr::null(),
                &mut key,
                std::ptr::null_mut(),
            )
        };
        check(status)?;
        Ok(Self(key))
    }

    /// Names of the binary values whose data is exactly `len` bytes.
    fn value_names_of_len(&self, len: usize) -> Result<Vec<String>, IoError> {
        let mut names = Vec::new();
        let mut name = vec![0u16; 16384];
        for index in 0.. {
            let mut name_len = name.len() as u32;
            let mut kind = 0u32;
            let mut size = 0u32;
            #[allow(unsafe_code)]
            // SAFETY: `name` is writable for `name_len` characters; data is not requested.
            let status = unsafe {
                RegEnumValueW(
                    self.0,
                    index,
                    name.as_mut_ptr(),
                    &mut name_len,
                    std::ptr::null(),
                    &mut kind,
                    std::ptr::null_mut(),
                    &mut size,
                )
            };
            if status == ERROR_NO_MORE_ITEMS {
                break;
            }
            check(status)?;
            if kind == REG_BINARY && size as usize == len {
                names.push(String::from_utf16_lossy(&name[..name_len as usize]));
            }
        }
        Ok(names)
    }

    fn get(&self, name: &str) -> Result<Option<Vec<u8>>, IoError> {
        let name = wide(name);
        let mut size = 0u32;
        #[allow(unsafe_code)]
        // SAFETY: asks only for the size.
        let status = unsafe {
            RegQueryValueExW(
                self.0,
                name.as_ptr(),
                std::ptr::null(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                &mut size,
            )
        };
        if status == ERROR_FILE_NOT_FOUND {
            return Ok(None);
        }
        check(status)?;
        let mut data = vec![0u8; size as usize];
        #[allow(unsafe_code)]
        // SAFETY: `data` is writable for `size` bytes.
        let status = unsafe {
            RegQueryValueExW(
                self.0,
                name.as_ptr(),
                std::ptr::null(),
                std::ptr::null_mut(),
                data.as_mut_ptr(),
                &mut size,
            )
        };
        check(status)?;
        data.truncate(size as usize);
        Ok(Some(data))
    }

    fn set(&self, name: &str, data: &[u8]) -> Result<(), IoError> {
        let name = wide(name);
        #[allow(unsafe_code)]
        // SAFETY: `name` is NUL-terminated and `data` readable for its length.
        let status = unsafe {
            RegSetValueExW(
                self.0,
                name.as_ptr(),
                0,
                REG_BINARY,
                data.as_ptr(),
                data.len() as u32,
            )
        };
        check(status)
    }

    fn delete(&self, name: &str) -> Result<(), IoError> {
        let name = wide(name);
        #[allow(unsafe_code)]
        // SAFETY: `name` is NUL-terminated.
        let status = unsafe { RegDeleteValueW(self.0, name.as_ptr()) };
        if status == ERROR_FILE_NOT_FOUND {
            return Ok(());
        }
        check(status)
    }

    /// Make the key's changes durable before returning.
    fn flush(&self) -> Result<(), IoError> {
        #[allow(unsafe_code)]
        // SAFETY: `self.0` is an open key.
        check(unsafe { RegFlushKey(self.0) })
    }
}

impl Drop for Key {
    fn drop(&mut self) {
        #[allow(unsafe_code)]
        // SAFETY: closes the key opened in `open`/`create`, once.
        unsafe {
            RegCloseKey(self.0);
        }
    }
}

fn check(status: u32) -> Result<(), IoError> {
    if status == ERROR_SUCCESS {
        Ok(())
    } else {
        Err(IoError::Os(status as i32))
    }
}

fn encode_plan(remap: &Remap, plan: &Plan) -> Vec<u8> {
    let mut out = remap.header();
    for (name, data) in plan {
        let name: Vec<u16> = name.encode_utf16().collect();
        out.extend_from_slice(&(name.len() as u16).to_le_bytes());
        out.extend(name.iter().flat_map(|c| c.to_le_bytes()));
        out.extend_from_slice(data);
    }
    out
}

/// The saved plan, if it was made for exactly this remap.
fn decode_plan(remap: &Remap, saved: &[u8]) -> Option<Plan> {
    let header = remap.header();
    let mut rest = saved.strip_prefix(header.as_slice())?;
    let mut plan = Vec::new();
    while !rest.is_empty() {
        let len = usize::from(u16::from_le_bytes([*rest.first()?, *rest.get(1)?]));
        let name_end = 2 + 2 * len;
        let units: Vec<u16> = rest
            .get(2..name_end)?
            .chunks_exact(2)
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .collect();
        let data: [u8; MBR_ID_LEN] = rest.get(name_end..name_end + MBR_ID_LEN)?.try_into().ok()?;
        plan.push((String::from_utf16(&units).ok()?, data));
        rest = &rest[name_end + MBR_ID_LEN..];
    }
    Some(plan)
}

/// Point every MountedDevices value that names a moved partition at its new start. Call while
/// the partitions are out of the table, before they are put back. Safe to call again after a
/// crash, until [`finish`] is called. Returns how many values were set.
pub fn remap(remap: &Remap) -> Result<usize, IoError> {
    let devices = Key::open(MOUNTED_DEVICES, KEY_READ | KEY_WRITE)?;
    let pending = Key::create(PLAN_KEY)?;
    let plan = match pending
        .get(PLAN_VALUE)?
        .and_then(|saved| decode_plan(remap, &saved))
    {
        Some(plan) => plan,
        None => {
            let mut plan = Plan::new();
            for name in devices.value_names_of_len(MBR_ID_LEN)? {
                let Some(data) = devices.get(&name)? else {
                    continue;
                };
                if let Some((_, new)) = remap.moves.iter().find(|(old, _)| data == remap.id(*old)) {
                    plan.push((name, remap.id(*new)));
                }
            }
            // A volume with no stored name gets one computed from its ID, so it would come
            // back with a new GUID. Store the old computed name for the new ID instead.
            for (old, new) in &remap.moves {
                let name = remap.computed_volume_name(*old);
                let stored = plan
                    .iter()
                    .any(|(n, d)| n.starts_with(VOLUME_PREFIX) && *d == remap.id(*new));
                if !stored && devices.get(&name)?.is_none() {
                    plan.push((name, remap.id(*new)));
                }
            }
            pending.set(PLAN_VALUE, &encode_plan(remap, &plan))?;
            pending.flush()?;
            plan
        }
    };
    for (name, data) in &plan {
        devices.set(name, data)?;
    }
    devices.flush()?;
    Ok(plan.len())
}

/// Forget the saved plan once the partitions are back in the table.
pub fn finish() -> Result<(), IoError> {
    match Key::open(PLAN_KEY, KEY_READ | KEY_WRITE) {
        Ok(key) => {
            key.delete(PLAN_VALUE)?;
            key.flush()
        }
        Err(IoError::Os(code)) if code == ERROR_FILE_NOT_FOUND as i32 => Ok(()),
        Err(e) => Err(e),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plan_round_trips_and_only_matches_its_own_remap() {
        let remap = Remap {
            signature: [1, 2, 3, 4],
            moves: vec![(1 << 20, 9 << 20), (2 << 20, 10 << 20)],
        };
        let plan = vec![
            ("\\DosDevices\\E:".to_owned(), remap.id(9 << 20)),
            ("\\??\\Volume{x}".to_owned(), remap.id(10 << 20)),
        ];
        let saved = encode_plan(&remap, &plan);
        assert_eq!(decode_plan(&remap, &saved), Some(plan));
        let other = Remap {
            moves: vec![(1 << 20, 8 << 20)],
            ..remap.clone()
        };
        assert_eq!(decode_plan(&other, &saved), None);
        assert_eq!(decode_plan(&remap, &saved[..saved.len() - 1]), None);
    }

    #[test]
    fn computed_name_matches_what_windows_shows() {
        // Seen on an Azure VM: signature 0xCF892F14, partition at 0x0F8000_0000 (E:).
        let remap = Remap {
            signature: 0xCF89_2F14u32.to_le_bytes(),
            moves: vec![],
        };
        assert_eq!(
            remap.computed_volume_name(0x0F_8000_0000),
            r"\??\Volume{cf892f14-0000-0000-0000-00800f000000}"
        );
        assert_eq!(
            remap.computed_volume_name(0x10_0000),
            r"\??\Volume{cf892f14-0000-0000-0000-100000000000}"
        );
    }

    #[test]
    fn id_is_signature_then_offset() {
        let remap = Remap {
            signature: [0x11, 0xEF, 0xAC, 0x6F],
            moves: vec![],
        };
        assert_eq!(
            remap.id(0x10_0000),
            [0x11, 0xEF, 0xAC, 0x6F, 0, 0, 0x10, 0, 0, 0, 0, 0]
        );
    }
}
