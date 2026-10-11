use serde::ser::SerializeStruct;
use serde::{Serialize, Serializer};

pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("You appear to be offline. {0}")]
    Network(String),
    #[error("The application catalog could not be read: {0}")]
    Catalog(String),
    #[error("\"{0}\" is not in the application catalog.")]
    UnknownApp(String),
    #[error("{0} is not available for download yet.")]
    NotAvailable(String),
    #[error("{0} does not support this computer's processor ({1}).")]
    UnsupportedArch(String, String),
    #[error("Blocked an untrusted download address: {0}")]
    Untrusted(String),
    #[error("The download failed its integrity check and was discarded. Expected SHA-256 {expected}, got {actual}.")]
    Checksum { expected: String, actual: String },
    #[error("The package is invalid: {0}")]
    Package(String),
    #[error("{0} is not installed.")]
    NotInstalled(String),
    #[error("Another operation is already running for {0}.")]
    Busy(String),
    #[error("{0} appears to be running. Close it and try again.")]
    AppRunning(String),
    #[error("Not enough free disk space: {needed} needed, {available} available.")]
    DiskSpace { needed: String, available: String },
    #[error("The operation was cancelled.")]
    Cancelled,
    #[error("{0}")]
    Invalid(String),
    #[error("File system error: {0}")]
    Io(#[from] std::io::Error),
    #[error("Data error: {0}")]
    Json(#[from] serde_json::Error),
}

impl Error {
    pub fn code(&self) -> &'static str {
        match self {
            Error::Network(_) => "network",
            Error::Catalog(_) => "catalog",
            Error::UnknownApp(_) => "unknown-app",
            Error::NotAvailable(_) => "not-available",
            Error::UnsupportedArch(..) => "unsupported-arch",
            Error::Untrusted(_) => "untrusted",
            Error::Checksum { .. } => "checksum",
            Error::Package(_) => "package",
            Error::NotInstalled(_) => "not-installed",
            Error::Busy(_) => "busy",
            Error::AppRunning(_) => "app-running",
            Error::DiskSpace { .. } => "disk-space",
            Error::Cancelled => "cancelled",
            Error::Invalid(_) => "invalid",
            Error::Io(_) => "io",
            Error::Json(_) => "json",
        }
    }
}

impl From<reqwest::Error> for Error {
    fn from(e: reqwest::Error) -> Self {
        // Strip the URL so messages stay readable; the cause is what matters.
        Error::Network(e.without_url().to_string())
    }
}

// Commands return errors to the UI as `{ code, message }`.
impl Serialize for Error {
    fn serialize<S: Serializer>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error> {
        let mut s = serializer.serialize_struct("Error", 2)?;
        s.serialize_field("code", self.code())?;
        s.serialize_field("message", &self.to_string())?;
        s.end()
    }
}
