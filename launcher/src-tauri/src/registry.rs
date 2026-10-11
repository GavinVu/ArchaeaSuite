//! The local record of installed applications. It holds everything needed to
//! show, launch and uninstall an app without the network or the catalog.

use crate::error::Result;
use crate::state::Paths;
use crate::util::{read_json, write_json_atomic};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

/// Written into every install folder. The launcher refuses to delete or
/// replace a folder that does not carry its own marker.
pub const MARKER_FILE: &str = ".archaea-app.json";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Marker {
    pub id: String,
    pub version: String,
}

pub fn read_marker(dir: &Path) -> Option<Marker> {
    read_json(&dir.join(MARKER_FILE))
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledApp {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub tagline: String,
    #[serde(default)]
    pub icon: Option<String>,
    pub version: String,
    pub arch: String,
    pub install_path: String,
    pub executable: String,
    pub installed_at: u64,
    #[serde(default)]
    pub size_bytes: u64,
    /// Set when the listing is produced: the executable has gone missing.
    #[serde(default, skip_deserializing)]
    pub broken: bool,
}

impl InstalledApp {
    pub fn dir(&self) -> PathBuf {
        PathBuf::from(&self.install_path)
    }
    pub fn exe_path(&self) -> PathBuf {
        self.dir().join(&self.executable)
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Registry {
    #[serde(default)]
    pub apps: BTreeMap<String, InstalledApp>,
}

pub fn load(paths: &Paths) -> Registry {
    read_json(&paths.registry_file()).unwrap_or_default()
}

pub fn save(paths: &Paths, registry: &Registry) -> Result<()> {
    write_json_atomic(&paths.registry_file(), registry)
}
