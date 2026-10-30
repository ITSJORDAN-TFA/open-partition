use std::fmt;

use opm_core::{IoError, MoveError};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlatformError {
    Io(IoError),
    /// A safety rule stopped the operation before anything was written.
    Refused(String),
    Move(MoveError),
}

impl fmt::Display for PlatformError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            PlatformError::Io(e) => write!(f, "{e}"),
            PlatformError::Refused(why) => write!(f, "refused: {why}"),
            PlatformError::Move(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for PlatformError {}

impl From<IoError> for PlatformError {
    fn from(e: IoError) -> Self {
        PlatformError::Io(e)
    }
}

impl From<MoveError> for PlatformError {
    fn from(e: MoveError) -> Self {
        PlatformError::Move(e)
    }
}
