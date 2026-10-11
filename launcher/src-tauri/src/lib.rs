mod catalog;
mod error;
mod installer;
mod registry;
mod settings;
mod state;
mod trust;
mod util;

use error::{Error, Result};
use registry::InstalledApp;
use serde::Serialize;
use settings::Settings;
use state::{Ctx, Paths};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, RwLock};
use tauri::{AppHandle, Emitter, Manager, State};

const PROGRESS_EVENT: &str = "install-progress";

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SystemInfo {
    arch: &'static str,
    launcher_version: &'static str,
    default_install_dir: String,
    catalog_url: String,
    dev_catalog: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct AppStorage {
    id: String,
    name: String,
    bytes: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StorageInfo {
    install_dir: String,
    free_bytes: Option<u64>,
    total_bytes: Option<u64>,
    apps_bytes: u64,
    cache_bytes: u64,
    apps: Vec<AppStorage>,
}

#[tauri::command]
fn get_system_info(ctx: State<'_, Ctx>) -> SystemInfo {
    SystemInfo {
        arch: installer::host_arch(),
        launcher_version: env!("CARGO_PKG_VERSION"),
        default_install_dir: ctx.paths.default_install_dir.to_string_lossy().into_owned(),
        catalog_url: trust::catalog_url(),
        dev_catalog: trust::dev_catalog_url().is_some(),
    }
}

#[tauri::command]
async fn load_catalog(ctx: State<'_, Ctx>, refresh: bool) -> Result<catalog::CatalogResult> {
    Ok(catalog::load(&ctx, refresh).await)
}

#[tauri::command(async)]
fn get_installed(ctx: State<'_, Ctx>) -> Vec<InstalledApp> {
    let apps: Vec<InstalledApp> = ctx.registry.lock().unwrap().apps.values().cloned().collect();
    apps.into_iter()
        .map(|mut app| {
            app.broken = !app.exe_path().is_file();
            app
        })
        .collect()
}

#[tauri::command]
async fn install_app(app: AppHandle, ctx: State<'_, Ctx>, id: String) -> Result<InstalledApp> {
    let progress: Arc<installer::ProgressFn> = Arc::new(move |p| {
        let _ = app.emit(PROGRESS_EVENT, p);
    });
    installer::install(&ctx, &id, progress).await
}

#[tauri::command]
fn cancel_install(ctx: State<'_, Ctx>, id: String) {
    installer::cancel(&ctx, &id);
}

#[tauri::command(async)]
fn uninstall_app(ctx: State<'_, Ctx>, id: String) -> Result<()> {
    installer::uninstall(&ctx, &id)
}

#[tauri::command(async)]
fn launch_app(ctx: State<'_, Ctx>, id: String) -> Result<()> {
    installer::launch(&ctx, &id)
}

#[tauri::command(async)]
fn open_app_folder(ctx: State<'_, Ctx>, id: String) -> Result<()> {
    let dir = ctx
        .registry
        .lock()
        .unwrap()
        .apps
        .get(&id)
        .map(|a| a.dir())
        .ok_or_else(|| Error::NotInstalled(id.clone()))?;
    open_folder(dir)
}

#[tauri::command(async)]
fn open_install_dir(ctx: State<'_, Ctx>) -> Result<()> {
    let dir = PathBuf::from(&ctx.settings.lock().unwrap().install_dir);
    std::fs::create_dir_all(&dir)?;
    open_folder(dir)
}

fn open_folder(dir: PathBuf) -> Result<()> {
    if !dir.is_dir() {
        return Err(Error::Invalid(format!("{} no longer exists.", dir.display())));
    }
    std::process::Command::new("explorer").arg(&dir).spawn()?;
    Ok(())
}

#[tauri::command]
fn get_settings(ctx: State<'_, Ctx>) -> Settings {
    ctx.settings.lock().unwrap().clone()
}

#[tauri::command(async)]
fn set_settings(ctx: State<'_, Ctx>, settings: Settings) -> Result<Settings> {
    let mut next = settings;
    next.install_dir = next.install_dir.trim().to_string();
    if next.install_dir.is_empty() {
        next.install_dir = ctx.paths.default_install_dir.to_string_lossy().into_owned();
    }
    settings::validate(&next)?;
    settings::save(&ctx.paths, &next)?;
    *ctx.settings.lock().unwrap() = next.clone();
    Ok(next)
}

#[tauri::command(async)]
fn get_storage_info(ctx: State<'_, Ctx>) -> StorageInfo {
    let install_dir = ctx.settings.lock().unwrap().install_dir.clone();
    let installed: Vec<InstalledApp> =
        ctx.registry.lock().unwrap().apps.values().cloned().collect();
    let apps: Vec<AppStorage> = installed
        .iter()
        .map(|a| AppStorage {
            id: a.id.clone(),
            name: a.name.clone(),
            bytes: util::dir_size(&a.dir()),
        })
        .collect();
    let dir = PathBuf::from(&install_dir);
    StorageInfo {
        free_bytes: installer::free_space(&dir),
        total_bytes: installer::total_space(&dir),
        apps_bytes: apps.iter().map(|a| a.bytes).sum(),
        cache_bytes: util::dir_size(&ctx.paths.cache_dir()),
        apps,
        install_dir,
    }
}

/// Deletes the cached catalog and any leftover downloads. The catalog that
/// is already loaded stays in memory for the rest of the session.
#[tauri::command(async)]
fn clear_cache(ctx: State<'_, Ctx>) -> Result<()> {
    if !ctx.ops.lock().unwrap().is_empty() {
        return Err(Error::Invalid(
            "Wait for running installs to finish before clearing the cache.".into(),
        ));
    }
    let dir = ctx.paths.cache_dir();
    if dir.exists() {
        std::fs::remove_dir_all(dir)?;
    }
    Ok(())
}

fn focus_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            focus_main_window(app);
        }))
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .setup(|app| {
            let resolver = app.path();
            let paths = Paths {
                data_dir: resolver.app_local_data_dir()?,
                default_install_dir: resolver.local_data_dir()?.join("ArchaeaSuite").join("Apps"),
            };
            std::fs::create_dir_all(&paths.data_dir)?;
            let ctx = Ctx {
                http: state::build_http_client(),
                settings: Mutex::new(settings::load(&paths)),
                registry: Mutex::new(registry::load(&paths)),
                catalog: RwLock::new(None),
                ops: Mutex::new(HashMap::new()),
                paths,
            };
            installer::recover(&ctx);
            app.manage(ctx);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_system_info,
            load_catalog,
            get_installed,
            install_app,
            cancel_install,
            uninstall_app,
            launch_app,
            open_app_folder,
            open_install_dir,
            get_settings,
            set_settings,
            get_storage_info,
            clear_cache,
        ])
        .run(tauri::generate_context!())
        .expect("error while running ArchaeaSuite Launcher");
}
