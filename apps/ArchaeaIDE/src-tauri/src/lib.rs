//! ArchaeaIDE backend. The interface is deliberately small and generic: files,
//! search, pseudo-terminals, child processes that speak a `Content-Length`
//! framed protocol (language servers and debug adapters), one-shot commands
//! (git, formatters) and a loopback-only client for a local AI server.
//! Everything the IDE *does* with these lives in the frontend.

mod ai;
mod config;
mod files;
mod proc;
mod pty;
mod rpc;
mod search;

use tauri::{Manager, RunEvent};

pub type Res<T> = Result<T, String>;

pub fn err<E: std::fmt::Display>(e: E) -> String {
    e.to_string()
}

#[derive(Default)]
pub struct State {
    pub ptys: pty::Ptys,
    pub rpcs: rpc::Rpcs,
    pub ai: ai::Chats,
}

/// A file or folder passed on the command line (`ArchaeaIDE.exe <path>`).
#[tauri::command]
fn startup_path() -> Option<String> {
    std::env::args().nth(1).filter(|a| !a.starts_with('-') && std::path::Path::new(a).exists())
}

#[tauri::command]
fn app_info(app: tauri::AppHandle) -> serde_json::Value {
    serde_json::json!({
        "version": app.package_info().version.to_string(),
        "arch": if cfg!(target_arch = "aarch64") { "arm64" } else { "x64" },
        "dataDir": config::data_dir().to_string_lossy(),
        "home": std::env::var("USERPROFILE").unwrap_or_default(),
        "debug": cfg!(debug_assertions),
    })
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(State::default())
        .invoke_handler(tauri::generate_handler![
            startup_path,
            app_info,
            config::load_config,
            config::save_config,
            files::list_dir,
            files::read_text,
            files::write_text,
            files::create_file,
            files::create_dir,
            files::rename_path,
            files::trash_path,
            files::stat_path,
            search::list_files,
            search::search,
            search::replace_all,
            pty::pty_spawn,
            pty::pty_write,
            pty::pty_resize,
            pty::pty_kill,
            rpc::rpc_start,
            rpc::rpc_send,
            rpc::rpc_stop,
            proc::exec,
            proc::which,
            ai::ai_models,
            ai::ai_chat,
            ai::ai_cancel,
            ai::ai_start,
        ])
        .build(tauri::generate_context!())
        .expect("failed to start ArchaeaIDE")
        .run(|app, event| {
            // Nothing the IDE started may outlive it.
            if let RunEvent::Exit = event {
                let state = app.state::<State>();
                state.ptys.kill_all();
                state.rpcs.kill_all();
            }
        });
}
