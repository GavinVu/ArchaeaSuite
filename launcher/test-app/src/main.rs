//! Archaea Hello — the smallest possible "real" application.
//!
//! It exists so the launcher's install, launch, update and uninstall paths can
//! be tested against a genuine GitHub release. It shows its version and how
//! many times it has been launched. The launch counter lives in `config/`,
//! which the catalog lists under `preserve`, so it survives updates — the
//! same mechanism real apps use to keep settings.

#![windows_subsystem = "windows"]

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;

/// Set `HELLO_VERSION` at build time to produce a differently-versioned build.
const VERSION: &str = match option_env!("HELLO_VERSION") {
    Some(v) => v,
    None => env!("CARGO_PKG_VERSION"),
};

#[link(name = "user32")]
extern "system" {
    fn MessageBoxW(hwnd: *mut core::ffi::c_void, text: *const u16, caption: *const u16, kind: u32) -> i32;
}

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

fn bump_launch_count() -> Option<u32> {
    let dir = std::env::current_exe().ok()?.parent()?.join("config");
    std::fs::create_dir_all(&dir).ok()?;
    let file = dir.join("launch-count.txt");
    let count = std::fs::read_to_string(&file)
        .ok()
        .and_then(|s| s.trim().parse::<u32>().ok())
        .unwrap_or(0)
        + 1;
    std::fs::write(&file, count.to_string()).ok()?;
    Some(count)
}

fn main() {
    let launches = match bump_launch_count() {
        Some(1) => "This is its first launch.".to_string(),
        Some(n) => format!("It has been launched {n} times; the count is kept across updates."),
        None => "The launch counter could not be saved.".to_string(),
    };
    let text = wide(&format!(
        "Archaea Hello {VERSION}\n\nInstalled and launched by ArchaeaSuite Launcher.\n{launches}"
    ));
    let caption = wide("Archaea Hello");
    // MB_OK | MB_ICONINFORMATION
    unsafe { MessageBoxW(std::ptr::null_mut(), text.as_ptr(), caption.as_ptr(), 0x40) };
}
