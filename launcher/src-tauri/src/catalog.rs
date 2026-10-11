//! The application catalog: a JSON document hosted on GitHub that describes
//! every ArchaeaSuite app. New apps are released by editing that file — the
//! launcher never needs rebuilding.

use crate::error::{Error, Result};
use crate::state::Ctx;
use crate::trust;
use crate::util::{now_secs, read_json, safe_relative_path, write_json_atomic};
use base64::Engine;
use futures_util::future::join_all;
use reqwest::Url;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};
use std::time::Duration;

/// Highest catalog `schemaVersion` this launcher understands.
pub const SUPPORTED_SCHEMA: u32 = 1;

const MAX_CATALOG_BYTES: usize = 2 * 1024 * 1024;
const MAX_ICON_BYTES: usize = 512 * 1024;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum AppStatus {
    #[default]
    Available,
    ComingSoon,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Package {
    pub url: String,
    pub sha256: String,
    /// Download size in bytes.
    #[serde(default)]
    pub size: u64,
    /// Size on disk once extracted, if known.
    #[serde(default)]
    pub installed_size: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogApp {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub tagline: String,
    #[serde(default)]
    pub description: String,
    /// In the hosted catalog: a URL (absolute, or relative to the catalog).
    /// Once loaded by the launcher: a `data:` URL, so icons work offline.
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub category: String,
    #[serde(default)]
    pub status: AppStatus,
    #[serde(default)]
    pub featured: bool,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub release_date: Option<String>,
    #[serde(default)]
    pub release_notes: Option<String>,
    #[serde(default)]
    pub homepage: Option<String>,
    /// Path of the program to launch, relative to the package root.
    #[serde(default)]
    pub executable: Option<String>,
    /// Keyed by architecture: `x64` or `arm64`.
    #[serde(default)]
    pub packages: BTreeMap<String, Package>,
    /// Paths inside the install folder that are carried over on update.
    #[serde(default)]
    pub preserve: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Catalog {
    pub schema_version: u32,
    #[serde(default)]
    pub updated: Option<String>,
    pub apps: Vec<CatalogApp>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawCatalog {
    schema_version: u32,
    #[serde(default)]
    updated: Option<String>,
    // Entries are parsed one by one so a single malformed app cannot take
    // the whole catalog down.
    #[serde(default)]
    apps: Vec<serde_json::Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CacheFile {
    fetched_at: u64,
    catalog: Catalog,
    /// Icon source URL -> data URL, so unchanged icons survive a flaky refresh.
    #[serde(default)]
    icons: HashMap<String, String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogResult {
    pub catalog: Option<Catalog>,
    /// `network`, `cache` or `none`.
    pub source: &'static str,
    pub fetched_at: Option<u64>,
    /// Why the network refresh failed, when it did.
    pub error: Option<String>,
    /// Entries that were skipped because they failed validation.
    pub warnings: Vec<String>,
}

fn valid_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 64
        && id
            .chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
        && !id.starts_with('-')
        && !id.ends_with('-')
}

/// Checks one catalog entry. Anything installable must be fully specified
/// and point only at trusted download locations.
pub fn validate_app(app: &CatalogApp) -> std::result::Result<(), String> {
    if !valid_id(&app.id) {
        return Err(format!("invalid id {:?}", app.id));
    }
    if app.name.trim().is_empty() {
        return Err(format!("{}: missing name", app.id));
    }
    if let Some(home) = &app.homepage {
        let ok = Url::parse(home).map(|u| u.scheme() == "https").unwrap_or(false);
        if !ok {
            return Err(format!("{}: homepage must be an https URL", app.id));
        }
    }
    if app.status == AppStatus::ComingSoon {
        return Ok(());
    }
    let version = app.version.as_deref().unwrap_or("");
    if semver::Version::parse(version).is_err() {
        return Err(format!("{}: version {version:?} is not valid semver", app.id));
    }
    let exe = app.executable.as_deref().unwrap_or("");
    if safe_relative_path(exe).is_none() || !exe.to_ascii_lowercase().ends_with(".exe") {
        return Err(format!("{}: executable {exe:?} is not a safe .exe path", app.id));
    }
    if app.packages.is_empty() {
        return Err(format!("{}: no packages", app.id));
    }
    for (arch, pkg) in &app.packages {
        if arch != "x64" && arch != "arm64" {
            return Err(format!("{}: unknown architecture {arch:?}", app.id));
        }
        let trusted = Url::parse(&pkg.url)
            .map(|u| trust::is_trusted_package_url(&u))
            .unwrap_or(false);
        if !trusted {
            return Err(format!("{}: untrusted package URL {}", app.id, pkg.url));
        }
        if pkg.sha256.len() != 64 || !pkg.sha256.chars().all(|c| c.is_ascii_hexdigit()) {
            return Err(format!("{}: {arch} package has no valid SHA-256", app.id));
        }
    }
    for p in &app.preserve {
        if safe_relative_path(p).is_none() {
            return Err(format!("{}: unsafe preserve path {p:?}", app.id));
        }
    }
    Ok(())
}

/// Parses and validates catalog JSON. Returns the usable catalog plus a
/// warning for every entry that had to be skipped.
pub fn parse(bytes: &[u8]) -> Result<(Catalog, Vec<String>)> {
    let raw: RawCatalog =
        serde_json::from_slice(bytes).map_err(|e| Error::Catalog(e.to_string()))?;
    if raw.schema_version == 0 || raw.schema_version > SUPPORTED_SCHEMA {
        return Err(Error::Catalog(format!(
            "it uses format version {}, which needs a newer launcher. Update ArchaeaSuite Launcher from Settings.",
            raw.schema_version
        )));
    }
    let mut apps: Vec<CatalogApp> = Vec::new();
    let mut warnings = Vec::new();
    for value in raw.apps {
        let app: CatalogApp = match serde_json::from_value(value) {
            Ok(app) => app,
            Err(e) => {
                warnings.push(format!("skipped a malformed entry: {e}"));
                continue;
            }
        };
        if let Err(reason) = validate_app(&app) {
            warnings.push(format!("skipped {reason}"));
            continue;
        }
        if apps.iter().any(|a| a.id == app.id) {
            warnings.push(format!("skipped duplicate id {:?}", app.id));
            continue;
        }
        apps.push(app);
    }
    Ok((
        Catalog {
            schema_version: raw.schema_version,
            updated: raw.updated,
            apps,
        },
        warnings,
    ))
}

async fn get_limited(ctx: &Ctx, url: Url, limit: usize) -> Result<Vec<u8>> {
    use futures_util::StreamExt;
    let resp = ctx
        .http
        .get(url)
        .timeout(Duration::from_secs(25))
        .send()
        .await?
        .error_for_status()?;
    let mut out = Vec::new();
    let mut stream = resp.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk?;
        if out.len() + chunk.len() > limit {
            return Err(Error::Catalog("a response was larger than allowed".into()));
        }
        out.extend_from_slice(&chunk);
    }
    Ok(out)
}

fn image_mime(bytes: &[u8]) -> Option<&'static str> {
    if bytes.starts_with(&[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A]) {
        Some("image/png")
    } else if bytes.starts_with(&[0xFF, 0xD8, 0xFF]) {
        Some("image/jpeg")
    } else if bytes.len() > 12 && &bytes[0..4] == b"RIFF" && &bytes[8..12] == b"WEBP" {
        Some("image/webp")
    } else {
        None
    }
}

async fn fetch_icon(ctx: &Ctx, url: Url) -> Option<String> {
    let bytes = get_limited(ctx, url, MAX_ICON_BYTES).await.ok()?;
    let mime = image_mime(&bytes)?;
    Some(format!(
        "data:{mime};base64,{}",
        base64::engine::general_purpose::STANDARD.encode(bytes)
    ))
}

/// Replaces each app's icon URL with an embedded `data:` URL.
async fn embed_icons(
    ctx: &Ctx,
    base: &Url,
    catalog: &mut Catalog,
    previous: &HashMap<String, String>,
) -> HashMap<String, String> {
    let sources: Vec<Option<Url>> = catalog
        .apps
        .iter()
        .map(|app| {
            let raw = app.icon.as_deref()?;
            let url = base.join(raw).ok()?;
            trust::is_trusted_asset_url(&url).then_some(url)
        })
        .collect();

    let fetched = join_all(sources.iter().map(|src| async move {
        match src {
            Some(url) => fetch_icon(ctx, url.clone()).await,
            None => None,
        }
    }))
    .await;

    let mut icons = HashMap::new();
    for ((app, src), data) in catalog.apps.iter_mut().zip(&sources).zip(fetched) {
        app.icon = match src {
            Some(url) => {
                let key = url.to_string();
                let data = data.or_else(|| previous.get(&key).cloned());
                if let Some(d) = &data {
                    icons.insert(key, d.clone());
                }
                data
            }
            None => None,
        };
    }
    icons
}

fn load_cache(ctx: &Ctx) -> Option<CacheFile> {
    let cache: CacheFile = read_json(&ctx.paths.catalog_cache_file())?;
    (cache.catalog.schema_version <= SUPPORTED_SCHEMA).then_some(cache)
}

async fn fetch_remote(ctx: &Ctx) -> Result<(CacheFile, Vec<String>)> {
    let url = Url::parse(&trust::catalog_url())
        .map_err(|e| Error::Catalog(format!("bad catalog address: {e}")))?;
    let bytes = get_limited(ctx, url.clone(), MAX_CATALOG_BYTES).await?;
    let (mut catalog, warnings) = parse(&bytes)?;
    let previous = load_cache(ctx).map(|c| c.icons).unwrap_or_default();
    let icons = embed_icons(ctx, &url, &mut catalog, &previous).await;
    Ok((
        CacheFile {
            fetched_at: now_secs(),
            catalog,
            icons,
        },
        warnings,
    ))
}

/// Loads the catalog. With `refresh` it is fetched from GitHub and cached;
/// if that fails — or without `refresh` — the cached copy is returned so the
/// launcher stays usable offline.
pub async fn load(ctx: &Ctx, refresh: bool) -> CatalogResult {
    let mut error = None;
    if refresh {
        match fetch_remote(ctx).await {
            Ok((cache, warnings)) => {
                let _ = write_json_atomic(&ctx.paths.catalog_cache_file(), &cache);
                *ctx.catalog.write().unwrap() = Some(cache.catalog.clone());
                return CatalogResult {
                    catalog: Some(cache.catalog),
                    source: "network",
                    fetched_at: Some(cache.fetched_at),
                    error: None,
                    warnings,
                };
            }
            Err(e) => error = Some(e.to_string()),
        }
    }
    match load_cache(ctx) {
        Some(cache) => {
            *ctx.catalog.write().unwrap() = Some(cache.catalog.clone());
            CatalogResult {
                catalog: Some(cache.catalog),
                source: "cache",
                fetched_at: Some(cache.fetched_at),
                error,
                warnings: Vec::new(),
            }
        }
        None => CatalogResult {
            catalog: None,
            source: "none",
            fetched_at: None,
            error,
            warnings: Vec::new(),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const GOOD: &str = r#"{
        "id": "archaea-hello", "name": "Archaea Hello", "version": "1.2.0",
        "executable": "ArchaeaHello.exe",
        "packages": { "x64": {
            "url": "https://github.com/GavinVu/ArchaeaSuite/releases/download/hello-v1.2.0/h.zip",
            "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "size": 10 } }
    }"#;

    fn catalog_with(apps: &[&str]) -> String {
        format!(r#"{{ "schemaVersion": 1, "apps": [{}] }}"#, apps.join(","))
    }

    #[test]
    fn parses_a_valid_catalog() {
        let (catalog, warnings) = parse(catalog_with(&[GOOD]).as_bytes()).unwrap();
        assert!(warnings.is_empty(), "{warnings:?}");
        assert_eq!(catalog.apps.len(), 1);
        assert_eq!(catalog.apps[0].status, AppStatus::Available);
    }

    #[test]
    fn coming_soon_needs_no_package() {
        let soon = r#"{ "id": "archaea-photo", "name": "ArchaeaPhoto", "status": "coming-soon" }"#;
        let (catalog, warnings) = parse(catalog_with(&[soon]).as_bytes()).unwrap();
        assert!(warnings.is_empty());
        assert_eq!(catalog.apps[0].status, AppStatus::ComingSoon);
    }

    #[test]
    fn skips_bad_entries_but_keeps_good_ones() {
        let untrusted = GOOD
            .replace("archaea-hello", "evil")
            .replace("github.com/GavinVu", "github.com/Mallory");
        let traversal = GOOD
            .replace("archaea-hello", "sneaky")
            .replace("ArchaeaHello.exe", "../../Windows/System32/cmd.exe");
        let bad_hash = GOOD.replace("archaea-hello", "nohash").replace(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "abc",
        );
        let malformed = r#"{ "id": 42 }"#;
        let json = catalog_with(&[GOOD, &untrusted, &traversal, &bad_hash, malformed, GOOD]);
        let (catalog, warnings) = parse(json.as_bytes()).unwrap();
        assert_eq!(catalog.apps.len(), 1);
        assert_eq!(catalog.apps[0].id, "archaea-hello");
        assert_eq!(warnings.len(), 5, "{warnings:?}");
    }

    #[test]
    fn refuses_newer_schema() {
        let err = parse(br#"{ "schemaVersion": 99, "apps": [] }"#).unwrap_err();
        assert_eq!(err.code(), "catalog");
    }
}
