//! Reads the SMBIOS chassis asset tag, which Azure sets to a fixed value on every VM. The write
//! guard uses it so the engine itself, not only the GUI, knows it is running on Azure.

use windows_sys::Win32::System::SystemInformation::GetSystemFirmwareTable;

/// Chassis asset tag on every Azure VM.
pub const AZURE_ASSET_TAG: &str = "7783-7084-3265-9085-8269-3286-77";

const RSMB: u32 = u32::from_be_bytes(*b"RSMB");
/// RawSMBIOSData header: calling method, major, minor, DMI revision, then the table length.
const RAW_HEADER: usize = 8;
const CHASSIS: u8 = 3;
const CHASSIS_ASSET_TAG: usize = 0x08;

/// Whether this machine's SMBIOS says it is an Azure VM.
pub fn is_azure_vm() -> bool {
    raw_table().is_some_and(|t| {
        chassis_asset_tags(&t)
            .iter()
            .any(|tag| tag == AZURE_ASSET_TAG)
    })
}

fn raw_table() -> Option<Vec<u8>> {
    #[allow(unsafe_code)]
    // SAFETY: a null buffer with size 0 only asks for the required size.
    let size = unsafe { GetSystemFirmwareTable(RSMB, 0, std::ptr::null_mut(), 0) };
    if size == 0 {
        return None;
    }
    let mut buffer = vec![0u8; size as usize];
    #[allow(unsafe_code)]
    // SAFETY: `buffer` is writable for `size` bytes.
    let written = unsafe { GetSystemFirmwareTable(RSMB, 0, buffer.as_mut_ptr().cast(), size) };
    if written == 0 || written > size {
        return None;
    }
    buffer.truncate(written as usize);
    let length = u32::from_le_bytes(buffer.get(4..8)?.try_into().ok()?) as usize;
    buffer
        .get(RAW_HEADER..RAW_HEADER + length)
        .map(<[u8]>::to_vec)
}

/// Asset tag strings of every chassis (type 3) structure in an SMBIOS table.
fn chassis_asset_tags(table: &[u8]) -> Vec<String> {
    let mut tags = Vec::new();
    let mut at = 0;
    while at + 4 <= table.len() {
        let kind = table[at];
        let length = usize::from(table[at + 1]);
        if length < 4 || at + length > table.len() {
            break;
        }
        // Strings follow the formatted area, each NUL-terminated, the set ending with a second NUL.
        let strings_start = at + length;
        let mut end = strings_start;
        while end + 1 < table.len() && !(table[end] == 0 && table[end + 1] == 0) {
            end += 1;
        }
        let strings: Vec<&[u8]> = table[strings_start..end].split(|&b| b == 0).collect();
        if kind == CHASSIS && length > CHASSIS_ASSET_TAG {
            let index = usize::from(table[at + CHASSIS_ASSET_TAG]);
            if let Some(s) = index.checked_sub(1).and_then(|i| strings.get(i)) {
                tags.push(String::from_utf8_lossy(s).trim().to_owned());
            }
        }
        if kind == 127 {
            break; // end-of-table
        }
        at = end + 2;
    }
    tags
}

#[cfg(test)]
mod tests {
    use super::*;

    fn structure(kind: u8, formatted: &[u8], strings: &[&str]) -> Vec<u8> {
        let mut s = vec![kind, (4 + formatted.len()) as u8, 0, 0];
        s.extend_from_slice(formatted);
        for text in strings {
            s.extend_from_slice(text.as_bytes());
            s.push(0);
        }
        if strings.is_empty() {
            s.push(0);
        }
        s.push(0);
        s
    }

    #[test]
    fn finds_the_chassis_asset_tag() {
        // Chassis: manufacturer (string 1), type, version (2), serial (3), asset tag (4).
        let mut table = structure(0, &[1, 2, 0, 0], &["BIOS vendor", "1.0"]);
        table.extend(structure(
            CHASSIS,
            &[1, 3, 2, 3, 4],
            &["Microsoft", "7.0", "0000-1111", AZURE_ASSET_TAG],
        ));
        table.extend(structure(127, &[], &[]));
        assert_eq!(chassis_asset_tags(&table), vec![AZURE_ASSET_TAG.to_owned()]);
    }

    #[test]
    fn missing_or_empty_tag_is_not_azure() {
        let mut table = structure(CHASSIS, &[1, 3, 0, 0, 0], &["Dell Inc."]);
        table.extend(structure(127, &[], &[]));
        assert!(chassis_asset_tags(&table).is_empty());
        assert!(chassis_asset_tags(&[1, 2]).is_empty());
    }
}
