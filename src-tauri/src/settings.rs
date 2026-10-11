use crate::error::{Error, Result};
use crate::state::Paths;
use crate::util::{nonce, read_json, write_json_atomic};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Theme {
    #[default]
    System,
    Dark,
    Light,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    /// Folder new applications are installed into.
    pub install_dir: String,
    pub theme: Theme,
    /// Check the catalog for app updates on startup and periodically.
    pub auto_check_updates: bool,
    /// Show a system notification when updates are found.
    pub notify_updates: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            install_dir: String::new(),
            theme: Theme::System,
            auto_check_updates: true,
            notify_updates: true,
        }
    }
}

pub fn load(paths: &Paths) -> Settings {
    let mut settings: Settings = read_json(&paths.settings_file()).unwrap_or_default();
    if settings.install_dir.trim().is_empty() {
        settings.install_dir = paths.default_install_dir.to_string_lossy().into_owned();
    }
    settings
}

/// Rejects install directories the launcher cannot actually write to.
pub fn validate(settings: &Settings) -> Result<()> {
    let dir = Path::new(&settings.install_dir);
    if !dir.is_absolute() {
        return Err(Error::Invalid(
            "The installation folder must be a full path, such as D:\\ArchaeaSuite.".into(),
        ));
    }
    fs::create_dir_all(dir).map_err(|e| {
        Error::Invalid(format!("The installation folder could not be created: {e}"))
    })?;
    let probe = dir.join(format!(".archaea-write-test-{}", nonce()));
    fs::write(&probe, b"ok").map_err(|e| {
        Error::Invalid(format!("The installation folder is not writable: {e}"))
    })?;
    let _ = fs::remove_file(&probe);
    Ok(())
}

pub fn save(paths: &Paths, settings: &Settings) -> Result<()> {
    write_json_atomic(&paths.settings_file(), settings)
}
