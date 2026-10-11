//! Install, update, repair and uninstall.
//!
//! Every install follows the same staged pipeline, so a failure at any point
//! leaves the previous state untouched:
//!
//! 1. download the package to the launcher cache, hashing as it streams
//! 2. verify size and SHA-256 against the catalog
//! 3. extract into `<root>/.staging/<id>-<nonce>` with path-safety checks
//! 4. carry over any `preserve` paths from the existing install
//! 5. move the existing install to `<root>/.backup/…`, move staging into place
//! 6. record it in the registry — on any error, the backup is moved back

use crate::catalog::{AppStatus, CatalogApp, Package};
use crate::error::{Error, Result};
use crate::registry::{self, InstalledApp, Marker, MARKER_FILE};
use crate::state::Ctx;
use crate::trust;
use crate::util::{dir_size, format_bytes, nonce, now_secs, safe_relative_path, write_json_atomic};
use reqwest::Url;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::collections::BTreeSet;
use std::fs;
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

const STAGING_DIR: &str = ".staging";
const BACKUP_DIR: &str = ".backup";
const TRASH_DIR: &str = ".trash";

const MAX_ZIP_ENTRIES: usize = 200_000;
const MAX_EXTRACTED_BYTES: u64 = 64 * 1024 * 1024 * 1024;
const MAX_DOWNLOAD_BYTES: u64 = 16 * 1024 * 1024 * 1024;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Progress {
    pub id: String,
    /// `downloading`, `verifying`, `extracting` or `finalizing`.
    pub phase: &'static str,
    pub done: u64,
    pub total: u64,
}

pub type ProgressFn = dyn Fn(Progress) + Send + Sync;

pub fn host_arch() -> &'static str {
    if cfg!(target_arch = "aarch64") {
        "arm64"
    } else {
        "x64"
    }
}

/// Picks the package for this machine. Windows on ARM runs x64 programs
/// under emulation, so x64 is an acceptable fallback there.
pub fn pick_package(app: &CatalogApp) -> Option<(&'static str, &Package)> {
    let host = host_arch();
    if let Some(pkg) = app.packages.get(host) {
        return Some((host, pkg));
    }
    if host == "arm64" {
        return app.packages.get("x64").map(|pkg| ("x64", pkg));
    }
    None
}

/// Marks an app as busy for the lifetime of the guard.
struct OpGuard<'a> {
    ctx: &'a Ctx,
    id: String,
    cancel: Arc<AtomicBool>,
}

impl<'a> OpGuard<'a> {
    fn begin(ctx: &'a Ctx, id: &str, display: &str) -> Result<Self> {
        let mut ops = ctx.ops.lock().unwrap();
        if ops.contains_key(id) {
            return Err(Error::Busy(display.to_string()));
        }
        let cancel = Arc::new(AtomicBool::new(false));
        ops.insert(id.to_string(), cancel.clone());
        Ok(OpGuard {
            ctx,
            id: id.to_string(),
            cancel,
        })
    }
}

impl Drop for OpGuard<'_> {
    fn drop(&mut self) {
        self.ctx.ops.lock().unwrap().remove(&self.id);
    }
}

pub fn cancel(ctx: &Ctx, id: &str) {
    if let Some(flag) = ctx.ops.lock().unwrap().get(id) {
        flag.store(true, Ordering::SeqCst);
    }
}

fn check_cancel(cancel: &AtomicBool) -> Result<()> {
    if cancel.load(Ordering::SeqCst) {
        Err(Error::Cancelled)
    } else {
        Ok(())
    }
}

/// Nearest existing ancestor, for free-space queries on not-yet-created dirs.
fn existing_ancestor(path: &Path) -> PathBuf {
    let mut p = path.to_path_buf();
    while !p.exists() {
        if !p.pop() {
            break;
        }
    }
    p
}

pub fn free_space(path: &Path) -> Option<u64> {
    fs4::available_space(existing_ancestor(path)).ok()
}

pub fn total_space(path: &Path) -> Option<u64> {
    fs4::total_space(existing_ancestor(path)).ok()
}

fn ensure_space(path: &Path, needed: u64) -> Result<()> {
    if let Some(available) = free_space(path) {
        if available < needed {
            return Err(Error::DiskSpace {
                needed: format_bytes(needed),
                available: format_bytes(available),
            });
        }
    }
    Ok(())
}

/// A program that is running cannot be opened for writing on Windows.
fn is_in_use(exe: &Path) -> bool {
    if !exe.is_file() {
        return false;
    }
    match fs::OpenOptions::new().write(true).open(exe) {
        Ok(_) => false,
        Err(e) => matches!(e.raw_os_error(), Some(32) | Some(33) | Some(5)),
    }
}

fn in_use_or_io(e: io::Error, name: &str) -> Error {
    if matches!(e.raw_os_error(), Some(5) | Some(32) | Some(33)) {
        Error::AppRunning(name.to_string())
    } else {
        Error::Io(e)
    }
}

/// Streams a package to `dest`, enforcing the trusted-publisher rules, the
/// declared size and the SHA-256 from the catalog.
async fn download(
    ctx: &Ctx,
    id: &str,
    pkg: &Package,
    dest: &Path,
    cancel: &AtomicBool,
    progress: &ProgressFn,
) -> Result<()> {
    use futures_util::StreamExt;
    use tokio::io::AsyncWriteExt;

    let url = Url::parse(&pkg.url).map_err(|_| Error::Untrusted(pkg.url.clone()))?;
    if !trust::is_trusted_package_url(&url) {
        return Err(Error::Untrusted(pkg.url.clone()));
    }

    let resp = ctx.http.get(url).send().await?.error_for_status()?;
    if let Some(len) = resp.content_length() {
        if pkg.size > 0 && len != pkg.size {
            return Err(Error::Package(format!(
                "the download is {len} bytes but the catalog lists {}",
                pkg.size
            )));
        }
    }
    let total = if pkg.size > 0 {
        pkg.size
    } else {
        resp.content_length().unwrap_or(0)
    };
    let limit = if pkg.size > 0 { pkg.size } else { MAX_DOWNLOAD_BYTES };

    if let Some(parent) = dest.parent() {
        tokio::fs::create_dir_all(parent).await?;
    }
    let mut file = tokio::fs::File::create(dest).await?;
    let mut hasher = Sha256::new();
    let mut done: u64 = 0;
    let mut last_emit = Instant::now() - Duration::from_secs(1);
    let mut stream = resp.bytes_stream();

    while let Some(chunk) = stream.next().await {
        check_cancel(cancel)?;
        let chunk = chunk?;
        done += chunk.len() as u64;
        if done > limit {
            return Err(Error::Package("the download is larger than declared".into()));
        }
        hasher.update(&chunk);
        file.write_all(&chunk).await?;
        if last_emit.elapsed() >= Duration::from_millis(80) {
            last_emit = Instant::now();
            progress(Progress { id: id.to_string(), phase: "downloading", done, total });
        }
    }
    file.flush().await?;
    drop(file);
    progress(Progress { id: id.to_string(), phase: "verifying", done, total: total.max(done) });

    if pkg.size > 0 && done != pkg.size {
        return Err(Error::Package(format!(
            "the download ended early ({done} of {} bytes)",
            pkg.size
        )));
    }
    let actual = hex::encode(hasher.finalize());
    if !actual.eq_ignore_ascii_case(&pkg.sha256) {
        return Err(Error::Checksum {
            expected: pkg.sha256.to_ascii_lowercase(),
            actual,
        });
    }
    Ok(())
}

/// Extracts a ZIP into `dest`, refusing anything that could write outside it
/// (zip-slip), symlinks, duplicate entries and decompression bombs.
/// Returns the number of bytes written.
pub fn extract_zip(
    zip_path: &Path,
    dest: &Path,
    cancel: &AtomicBool,
    on_entry: &dyn Fn(u64, u64),
) -> Result<u64> {
    let bad = |e: zip::result::ZipError| Error::Package(format!("the archive is unreadable ({e})"));
    let file = io::BufReader::new(fs::File::open(zip_path)?);
    let mut archive = zip::ZipArchive::new(file).map_err(bad)?;
    let count = archive.len();
    if count == 0 {
        return Err(Error::Package("the archive is empty".into()));
    }
    if count > MAX_ZIP_ENTRIES {
        return Err(Error::Package("the archive has too many files".into()));
    }
    fs::create_dir_all(dest)?;

    let mut written: u64 = 0;
    for i in 0..count {
        check_cancel(cancel)?;
        let mut entry = archive.by_index(i).map_err(bad)?;
        let name = entry.name().to_string();
        let rel = safe_relative_path(&name)
            .ok_or_else(|| Error::Package(format!("it contains an unsafe path: {name:?}")))?;
        if entry.unix_mode().map(|m| m & 0o170000 == 0o120000).unwrap_or(false) {
            return Err(Error::Package(format!("it contains a symbolic link: {name:?}")));
        }
        let out = dest.join(&rel);
        if entry.is_dir() {
            fs::create_dir_all(&out)?;
        } else {
            if let Some(parent) = out.parent() {
                fs::create_dir_all(parent)?;
            }
            // create_new: a second entry for the same path is an error, not an overwrite.
            let mut f = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&out)
                .map_err(|e| {
                    if e.kind() == io::ErrorKind::AlreadyExists {
                        Error::Package(format!("it contains {name:?} more than once"))
                    } else {
                        Error::Io(e)
                    }
                })?;
            let budget = MAX_EXTRACTED_BYTES - written;
            written += io::copy(&mut (&mut entry).take(budget + 1), &mut f)?;
            if written > MAX_EXTRACTED_BYTES {
                return Err(Error::Package("it expands to an unreasonable size".into()));
            }
        }
        on_entry(i as u64 + 1, count as u64);
    }
    Ok(written)
}

fn copy_tree(src: &Path, dst: &Path) -> io::Result<()> {
    let meta = fs::symlink_metadata(src)?;
    if meta.is_dir() {
        fs::create_dir_all(dst)?;
        for entry in fs::read_dir(src)? {
            let entry = entry?;
            copy_tree(&entry.path(), &dst.join(entry.file_name()))?;
        }
    } else if meta.is_file() {
        if let Some(parent) = dst.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(src, dst)?;
    }
    Ok(())
}

/// The state needed to undo a directory swap.
#[derive(Debug)]
pub struct Swap {
    target: PathBuf,
    backup: Option<PathBuf>,
}

impl Swap {
    /// Removes the new install and puts the previous one back.
    pub fn rollback(self) {
        let _ = fs::remove_dir_all(&self.target);
        if let Some(backup) = &self.backup {
            let _ = fs::rename(backup, &self.target);
        }
    }

    /// Discards the previous install once the new one is committed.
    pub fn finish(self) {
        if let Some(backup) = &self.backup {
            let _ = fs::remove_dir_all(backup);
            if let Some(parent) = backup.parent() {
                let _ = fs::remove_dir(parent);
            }
        }
    }
}

/// Moves `staging` to `target`, first moving any existing install aside.
/// If the second move fails the first is undone before returning.
pub fn swap_in(staging: &Path, target: &Path, id: &str, name: &str) -> Result<Swap> {
    let root = target
        .parent()
        .ok_or_else(|| Error::Invalid("The installation folder is not valid.".into()))?;

    let mut backup = None;
    if target.exists() {
        let ours = registry::read_marker(target).map(|m| m.id == id).unwrap_or(false);
        if !ours {
            // An empty leftover folder is harmless; anything else is not ours to replace.
            fs::remove_dir(target).map_err(|_| {
                Error::Invalid(format!(
                    "The folder {} already exists and was not created by ArchaeaSuite. Move it or choose another installation folder.",
                    target.display()
                ))
            })?;
        } else {
            let backup_root = root.join(BACKUP_DIR);
            fs::create_dir_all(&backup_root)?;
            let b = backup_root.join(format!("{id}-{}", nonce()));
            fs::rename(target, &b).map_err(|e| in_use_or_io(e, name))?;
            backup = Some(b);
        }
    }

    if let Err(e) = fs::rename(staging, target) {
        if let Some(b) = &backup {
            let _ = fs::rename(b, target);
        }
        return Err(e.into());
    }
    Ok(Swap {
        target: target.to_path_buf(),
        backup,
    })
}

/// Everything the blocking half of an install needs.
pub struct InstallPlan {
    pub id: String,
    pub name: String,
    pub version: String,
    pub executable: String,
    pub preserve: Vec<String>,
    pub zip_path: PathBuf,
    pub target: PathBuf,
}

/// Steps 3–5 of the pipeline: extract to staging, validate, preserve user
/// files, swap into place. Leaves no staging directory behind on failure.
pub fn install_package(
    plan: &InstallPlan,
    cancel: &AtomicBool,
    progress: &ProgressFn,
) -> Result<Swap> {
    let root = plan
        .target
        .parent()
        .ok_or_else(|| Error::Invalid("The installation folder is not valid.".into()))?;
    let staging_root = root.join(STAGING_DIR);
    let staging = staging_root.join(format!("{}-{}", plan.id, nonce()));

    let result = (|| -> Result<Swap> {
        if is_in_use(&plan.target.join(&plan.executable)) {
            return Err(Error::AppRunning(plan.name.clone()));
        }
        fs::create_dir_all(&staging)?;
        extract_zip(&plan.zip_path, &staging, cancel, &|done, total| {
            progress(Progress { id: plan.id.clone(), phase: "extracting", done, total });
        })?;

        let exe_rel = safe_relative_path(&plan.executable)
            .ok_or_else(|| Error::Package("the executable path is unsafe".into()))?;
        if !staging.join(&exe_rel).is_file() {
            return Err(Error::Package(format!(
                "{} was not found inside it",
                plan.executable
            )));
        }

        progress(Progress { id: plan.id.clone(), phase: "finalizing", done: 0, total: 0 });
        if plan.target.is_dir() {
            for p in &plan.preserve {
                let Some(rel) = safe_relative_path(p) else { continue };
                let from = plan.target.join(&rel);
                if from.exists() {
                    copy_tree(&from, &staging.join(&rel))?;
                }
            }
        }
        write_json_atomic(
            &staging.join(MARKER_FILE),
            &Marker { id: plan.id.clone(), version: plan.version.clone() },
        )?;
        check_cancel(cancel)?;
        swap_in(&staging, &plan.target, &plan.id, &plan.name)
    })();

    if result.is_err() {
        let _ = fs::remove_dir_all(&staging);
    }
    let _ = fs::remove_dir(&staging_root);
    result
}

/// Installs the catalog version of an app. The same call performs a fresh
/// install, an update, or a repair of an existing install.
pub async fn install(ctx: &Ctx, id: &str, progress: Arc<ProgressFn>) -> Result<InstalledApp> {
    let app: CatalogApp = ctx
        .catalog
        .read()
        .unwrap()
        .as_ref()
        .and_then(|c| c.apps.iter().find(|a| a.id == id).cloned())
        .ok_or_else(|| Error::UnknownApp(id.to_string()))?;
    if app.status != AppStatus::Available {
        return Err(Error::NotAvailable(app.name.clone()));
    }
    let (arch, pkg) = pick_package(&app)
        .map(|(arch, pkg)| (arch, pkg.clone()))
        .ok_or_else(|| Error::UnsupportedArch(app.name.clone(), host_arch().to_string()))?;
    let version = app.version.clone().unwrap_or_default();
    let executable = app.executable.clone().unwrap_or_default();

    let guard = OpGuard::begin(ctx, id, &app.name)?;
    let cancel = guard.cancel.clone();

    // Updates and repairs stay where the app already lives.
    let existing = ctx.registry.lock().unwrap().apps.get(id).cloned();
    let target = match &existing {
        Some(installed) => installed.dir(),
        None => PathBuf::from(&ctx.settings.lock().unwrap().install_dir).join(id),
    };
    if !target.is_absolute() {
        return Err(Error::Invalid("The installation folder is not valid.".into()));
    }

    let downloads = ctx.paths.downloads_dir();
    ensure_space(&downloads, pkg.size)?;
    ensure_space(&target, pkg.installed_size.unwrap_or(pkg.size.saturating_mul(3)))?;

    let zip_path = downloads.join(format!("{id}-{version}-{arch}-{}.zip.part", nonce()));
    progress(Progress { id: id.to_string(), phase: "downloading", done: 0, total: pkg.size });

    let outcome = async {
        download(ctx, id, &pkg, &zip_path, &cancel, progress.as_ref()).await?;

        let plan = InstallPlan {
            id: id.to_string(),
            name: app.name.clone(),
            version: version.clone(),
            executable: executable.clone(),
            preserve: app.preserve.clone(),
            zip_path: zip_path.clone(),
            target: target.clone(),
        };
        let (cancel2, progress2) = (cancel.clone(), progress.clone());
        let swap = tokio::task::spawn_blocking(move || {
            install_package(&plan, &cancel2, progress2.as_ref())
        })
        .await
        .map_err(|e| Error::Invalid(format!("The installer stopped unexpectedly: {e}")))??;

        let installed = InstalledApp {
            id: id.to_string(),
            name: app.name.clone(),
            tagline: app.tagline.clone(),
            icon: app.icon.clone(),
            version: version.clone(),
            arch: arch.to_string(),
            install_path: target.to_string_lossy().into_owned(),
            executable: executable.clone(),
            installed_at: now_secs(),
            size_bytes: dir_size(&target),
            broken: false,
        };

        let saved = {
            let mut reg = ctx.registry.lock().unwrap();
            let previous = reg.apps.insert(id.to_string(), installed.clone());
            let saved = registry::save(&ctx.paths, &reg);
            if saved.is_err() {
                match previous {
                    Some(p) => reg.apps.insert(id.to_string(), p),
                    None => reg.apps.remove(id),
                };
            }
            saved
        };
        match saved {
            Ok(()) => {
                swap.finish();
                Ok(installed)
            }
            Err(e) => {
                swap.rollback();
                Err(e)
            }
        }
    }
    .await;

    let _ = tokio::fs::remove_file(&zip_path).await;
    drop(guard);
    outcome
}

/// Removes an installed app. Only folders carrying the launcher's own marker
/// for this app are ever deleted.
pub fn uninstall(ctx: &Ctx, id: &str) -> Result<()> {
    let app = ctx
        .registry
        .lock()
        .unwrap()
        .apps
        .get(id)
        .cloned()
        .ok_or_else(|| Error::NotInstalled(id.to_string()))?;
    let _guard = OpGuard::begin(ctx, id, &app.name)?;

    let dir = app.dir();
    if dir.exists() {
        let ours = registry::read_marker(&dir).map(|m| m.id == id).unwrap_or(false);
        if !ours {
            return Err(Error::Invalid(format!(
                "{} does not look like an ArchaeaSuite install, so it was left untouched.",
                dir.display()
            )));
        }
        if is_in_use(&app.exe_path()) {
            return Err(Error::AppRunning(app.name.clone()));
        }
        let root = dir
            .parent()
            .ok_or_else(|| Error::Invalid("The installation folder is not valid.".into()))?;
        let trash_root = root.join(TRASH_DIR);
        fs::create_dir_all(&trash_root)?;
        let trash = trash_root.join(format!("{id}-{}", nonce()));
        // Moving first makes the removal all-or-nothing: if any file is
        // locked the move fails and the install is left fully intact.
        fs::rename(&dir, &trash).map_err(|e| in_use_or_io(e, &app.name))?;
        let _ = fs::remove_dir_all(&trash);
        let _ = fs::remove_dir(&trash_root);
    }

    let mut reg = ctx.registry.lock().unwrap();
    reg.apps.remove(id);
    registry::save(&ctx.paths, &reg)
}

pub fn launch(ctx: &Ctx, id: &str) -> Result<()> {
    let app = ctx
        .registry
        .lock()
        .unwrap()
        .apps
        .get(id)
        .cloned()
        .ok_or_else(|| Error::NotInstalled(id.to_string()))?;
    if ctx.ops.lock().unwrap().contains_key(id) {
        return Err(Error::Busy(app.name.clone()));
    }
    if safe_relative_path(&app.executable).is_none() {
        return Err(Error::Invalid(format!("{}'s install record is damaged.", app.name)));
    }
    let exe = app.exe_path();
    if !exe.is_file() {
        return Err(Error::Invalid(format!(
            "{}'s files are missing or damaged. Use Repair to restore it.",
            app.name
        )));
    }
    std::process::Command::new(&exe)
        .current_dir(app.dir())
        .spawn()
        .map_err(|e| Error::Invalid(format!("{} could not be started: {e}", app.name)))?;
    Ok(())
}

/// Cleans up after a crash or power loss mid-install: discards staging and
/// trash folders, and restores any backup whose install went missing.
pub fn recover(ctx: &Ctx) {
    let reg = ctx.registry.lock().unwrap().clone();
    let mut roots: BTreeSet<PathBuf> = BTreeSet::new();
    roots.insert(PathBuf::from(&ctx.settings.lock().unwrap().install_dir));
    for app in reg.apps.values() {
        if let Some(parent) = app.dir().parent() {
            roots.insert(parent.to_path_buf());
        }
    }

    for root in roots {
        let _ = fs::remove_dir_all(root.join(STAGING_DIR));
        let _ = fs::remove_dir_all(root.join(TRASH_DIR));

        let backup_root = root.join(BACKUP_DIR);
        if let Ok(entries) = fs::read_dir(&backup_root) {
            for entry in entries.flatten() {
                let path = entry.path();
                let name = entry.file_name().to_string_lossy().into_owned();
                let target = name
                    .rsplit_once('-')
                    .and_then(|(id, _)| reg.apps.get(id))
                    .map(|app| app.dir());
                match target {
                    Some(target) if !target.exists() => {
                        let _ = fs::rename(&path, &target);
                    }
                    _ => {
                        let _ = fs::remove_dir_all(&path);
                    }
                }
            }
            let _ = fs::remove_dir(&backup_root);
        }
    }
    let _ = fs::remove_dir_all(ctx.paths.downloads_dir());
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use zip::write::SimpleFileOptions;

    fn make_zip(path: &Path, entries: &[(&str, &[u8])]) {
        let mut zip = zip::ZipWriter::new(fs::File::create(path).unwrap());
        for (name, data) in entries {
            if name.ends_with('/') {
                zip.add_directory(*name, SimpleFileOptions::default()).unwrap();
            } else {
                zip.start_file(*name, SimpleFileOptions::default()).unwrap();
                zip.write_all(data).unwrap();
            }
        }
        zip.finish().unwrap();
    }

    fn no_cancel() -> AtomicBool {
        AtomicBool::new(false)
    }

    fn plan(tmp: &Path, zip: &Path, version: &str, preserve: &[&str]) -> InstallPlan {
        InstallPlan {
            id: "demo".into(),
            name: "Demo".into(),
            version: version.into(),
            executable: "Demo.exe".into(),
            preserve: preserve.iter().map(|s| s.to_string()).collect(),
            zip_path: zip.to_path_buf(),
            target: tmp.join("apps").join("demo"),
        }
    }

    #[test]
    fn extracts_nested_files() {
        let tmp = tempfile::tempdir().unwrap();
        let zip = tmp.path().join("a.zip");
        make_zip(&zip, &[("Demo.exe", b"MZ"), ("data/", b""), ("data/x/y.txt", b"hello")]);
        let out = tmp.path().join("out");
        let written = extract_zip(&zip, &out, &no_cancel(), &|_, _| {}).unwrap();
        assert_eq!(written, 7);
        assert_eq!(fs::read(out.join("data/x/y.txt")).unwrap(), b"hello");
    }

    #[test]
    fn refuses_zip_slip() {
        for evil in ["../escape.txt", "a/../../escape.txt", "/abs.txt", "C:/abs.txt", "x.txt:ads"] {
            let tmp = tempfile::tempdir().unwrap();
            let zip = tmp.path().join("a.zip");
            make_zip(&zip, &[("ok.txt", b"ok"), (evil, b"pwned")]);
            let out = tmp.path().join("deep").join("out");
            let err = extract_zip(&zip, &out, &no_cancel(), &|_, _| {}).unwrap_err();
            assert_eq!(err.code(), "package", "{evil}");
            assert!(!tmp.path().join("escape.txt").exists());
            assert!(!tmp.path().join("deep").join("escape.txt").exists());
        }
    }

    #[test]
    fn refuses_garbage_archives() {
        let tmp = tempfile::tempdir().unwrap();
        let zip = tmp.path().join("a.zip");
        fs::write(&zip, b"this is not a zip file").unwrap();
        let err = extract_zip(&zip, &tmp.path().join("out"), &no_cancel(), &|_, _| {}).unwrap_err();
        assert_eq!(err.code(), "package");
    }

    #[test]
    fn fresh_install_then_update_preserves_user_files() {
        let tmp = tempfile::tempdir().unwrap();
        let v1 = tmp.path().join("v1.zip");
        make_zip(&v1, &[("Demo.exe", b"v1"), ("old-only.txt", b"x")]);
        let p1 = plan(tmp.path(), &v1, "1.0.0", &["config"]);
        install_package(&p1, &no_cancel(), &|_| {}).unwrap().finish();
        assert_eq!(fs::read(p1.target.join("Demo.exe")).unwrap(), b"v1");
        assert_eq!(registry::read_marker(&p1.target).unwrap().version, "1.0.0");

        // The user changes a setting between versions.
        fs::create_dir_all(p1.target.join("config")).unwrap();
        fs::write(p1.target.join("config/user.json"), b"mine").unwrap();

        let v2 = tmp.path().join("v2.zip");
        make_zip(&v2, &[("Demo.exe", b"v2"), ("config/user.json", b"default")]);
        let p2 = plan(tmp.path(), &v2, "2.0.0", &["config"]);
        install_package(&p2, &no_cancel(), &|_| {}).unwrap().finish();

        assert_eq!(fs::read(p2.target.join("Demo.exe")).unwrap(), b"v2");
        assert_eq!(fs::read(p2.target.join("config/user.json")).unwrap(), b"mine");
        assert!(!p2.target.join("old-only.txt").exists(), "stale files are removed");
        assert_eq!(registry::read_marker(&p2.target).unwrap().version, "2.0.0");
        let apps = tmp.path().join("apps");
        assert!(!apps.join(STAGING_DIR).exists());
        assert!(!apps.join(BACKUP_DIR).exists());
    }

    #[test]
    fn failed_update_leaves_previous_version_intact() {
        let tmp = tempfile::tempdir().unwrap();
        let v1 = tmp.path().join("v1.zip");
        make_zip(&v1, &[("Demo.exe", b"v1")]);
        let p1 = plan(tmp.path(), &v1, "1.0.0", &[]);
        install_package(&p1, &no_cancel(), &|_| {}).unwrap().finish();

        // Missing executable.
        let broken = tmp.path().join("broken.zip");
        make_zip(&broken, &[("readme.txt", b"no exe here")]);
        let err = install_package(&plan(tmp.path(), &broken, "2.0.0", &[]), &no_cancel(), &|_| {})
            .unwrap_err();
        assert_eq!(err.code(), "package");

        // Malicious archive.
        let evil = tmp.path().join("evil.zip");
        make_zip(&evil, &[("Demo.exe", b"v2"), ("../../evil.txt", b"x")]);
        assert!(install_package(&plan(tmp.path(), &evil, "2.0.0", &[]), &no_cancel(), &|_| {}).is_err());

        // Cancelled.
        let cancelled = AtomicBool::new(true);
        let err = install_package(&p1, &cancelled, &|_| {}).unwrap_err();
        assert_eq!(err.code(), "cancelled");

        assert_eq!(fs::read(p1.target.join("Demo.exe")).unwrap(), b"v1");
        assert_eq!(registry::read_marker(&p1.target).unwrap().version, "1.0.0");
        assert!(!tmp.path().join("apps").join(STAGING_DIR).exists());
    }

    #[test]
    fn rollback_restores_previous_version_after_swap() {
        let tmp = tempfile::tempdir().unwrap();
        let v1 = tmp.path().join("v1.zip");
        make_zip(&v1, &[("Demo.exe", b"v1")]);
        let p1 = plan(tmp.path(), &v1, "1.0.0", &[]);
        install_package(&p1, &no_cancel(), &|_| {}).unwrap().finish();

        let v2 = tmp.path().join("v2.zip");
        make_zip(&v2, &[("Demo.exe", b"v2")]);
        let swap = install_package(&plan(tmp.path(), &v2, "2.0.0", &[]), &no_cancel(), &|_| {}).unwrap();
        assert_eq!(fs::read(p1.target.join("Demo.exe")).unwrap(), b"v2");
        swap.rollback();
        assert_eq!(fs::read(p1.target.join("Demo.exe")).unwrap(), b"v1");
    }

    #[test]
    fn will_not_replace_a_foreign_folder() {
        let tmp = tempfile::tempdir().unwrap();
        let zip = tmp.path().join("v1.zip");
        make_zip(&zip, &[("Demo.exe", b"v1")]);
        let p = plan(tmp.path(), &zip, "1.0.0", &[]);
        fs::create_dir_all(&p.target).unwrap();
        fs::write(p.target.join("thesis.docx"), b"important").unwrap();
        let err = install_package(&p, &no_cancel(), &|_| {}).unwrap_err();
        assert_eq!(err.code(), "invalid");
        assert_eq!(fs::read(p.target.join("thesis.docx")).unwrap(), b"important");
    }
}
