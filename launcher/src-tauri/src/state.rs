use crate::catalog::Catalog;
use crate::registry::Registry;
use crate::settings::Settings;
use crate::trust;
use reqwest::redirect::Policy;
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex, RwLock};
use std::time::Duration;

/// Where the launcher keeps its own files. Installed apps live elsewhere,
/// in the user-configurable install directory.
#[derive(Debug, Clone)]
pub struct Paths {
    pub data_dir: PathBuf,
    pub default_install_dir: PathBuf,
}

impl Paths {
    pub fn settings_file(&self) -> PathBuf {
        self.data_dir.join("settings.json")
    }
    pub fn registry_file(&self) -> PathBuf {
        self.data_dir.join("installed.json")
    }
    pub fn catalog_cache_file(&self) -> PathBuf {
        self.cache_dir().join("catalog.json")
    }
    pub fn cache_dir(&self) -> PathBuf {
        self.data_dir.join("cache")
    }
    pub fn downloads_dir(&self) -> PathBuf {
        self.cache_dir().join("downloads")
    }
}

/// Shared backend state, managed by Tauri.
pub struct Ctx {
    pub paths: Paths,
    pub http: reqwest::Client,
    pub settings: Mutex<Settings>,
    pub registry: Mutex<Registry>,
    /// The most recently loaded catalog (from the network or the cache).
    pub catalog: RwLock<Option<Catalog>>,
    /// Apps with an install/update/uninstall in flight, and their cancel flags.
    pub ops: Mutex<HashMap<String, Arc<AtomicBool>>>,
}

pub fn build_http_client() -> reqwest::Client {
    let dev = trust::dev_catalog_url().is_some();
    reqwest::Client::builder()
        .user_agent(concat!("ArchaeaSuiteLauncher/", env!("CARGO_PKG_VERSION")))
        .https_only(!dev)
        .connect_timeout(Duration::from_secs(15))
        .read_timeout(Duration::from_secs(45))
        .redirect(Policy::custom(|attempt| {
            if attempt.previous().len() > 8 {
                attempt.error("too many redirects")
            } else if trust::is_trusted_redirect(attempt.url()) {
                attempt.follow()
            } else {
                attempt.error("redirected to an untrusted host")
            }
        }))
        .build()
        .expect("failed to build HTTP client")
}
