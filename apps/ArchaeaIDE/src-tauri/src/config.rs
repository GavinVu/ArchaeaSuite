//! Settings, keybindings and session state: JSON files in
//! `%APPDATA%\ArchaeaSuite\ArchaeaIDE` (`ARCHAEAIDE_DATA_DIR` overrides, used
//! by tests). Nothing is ever written into the install folder.

use crate::{err, Res};
use std::path::PathBuf;

const FILES: [&str; 3] = ["settings.json", "keybindings.json", "state.json"];

pub fn data_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("ARCHAEAIDE_DATA_DIR") {
        return PathBuf::from(dir);
    }
    let base = std::env::var("APPDATA").map(PathBuf::from).unwrap_or_else(|_| std::env::temp_dir());
    base.join("ArchaeaSuite").join("ArchaeaIDE")
}

fn file(name: &str) -> Res<PathBuf> {
    if !FILES.contains(&name) {
        return Err(format!("unknown config file: {name}"));
    }
    Ok(data_dir().join(name))
}

#[tauri::command]
pub fn load_config(name: String) -> Res<Option<String>> {
    match std::fs::read_to_string(file(&name)?) {
        Ok(text) => Ok(Some(text)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(err(e)),
    }
}

#[tauri::command]
pub fn save_config(name: String, text: String) -> Res<()> {
    let path = file(&name)?;
    std::fs::create_dir_all(data_dir()).map_err(err)?;
    // Write beside the target and rename, so a crash never leaves half a file.
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, text).map_err(err)?;
    std::fs::rename(&tmp, &path).map_err(err)
}
