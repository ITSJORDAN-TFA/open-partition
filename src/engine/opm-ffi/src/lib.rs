//! C ABI exported as `opm_engine.dll`. The .NET service P/Invokes these functions.

/// ABI version. Bump on any breaking change to an exported signature; the service refuses
/// to load an engine whose ABI version it doesn't know.
pub const ABI_VERSION: u32 = 1;

#[allow(unsafe_code)] // `no_mangle` is an unsafe attribute in edition 2024 lint terms.
#[no_mangle]
pub extern "C" fn opm_abi_version() -> u32 {
    ABI_VERSION
}

#[cfg(test)]
mod tests {
    #[test]
    fn abi_version_is_exported() {
        assert_eq!(super::opm_abi_version(), super::ABI_VERSION);
    }
}
