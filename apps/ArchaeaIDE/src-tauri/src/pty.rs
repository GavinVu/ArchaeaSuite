//! Pseudo-terminals (ConPTY through `portable-pty`). The integrated terminal
//! and the Run button both use these; output is streamed to the frontend as
//! base64 so partial UTF-8 sequences survive chunk boundaries.

use crate::{err, Res, State};
use base64::Engine;
use portable_pty::{native_pty_system, ChildKiller, CommandBuilder, MasterPty, PtySize};
use serde::Serialize;
use std::collections::HashMap;
use std::io::{Read, Write};
use std::sync::Mutex;
use tauri::{AppHandle, Emitter, Manager};

struct Session {
    master: Box<dyn MasterPty + Send>,
    writer: Box<dyn Write + Send>,
    killer: Box<dyn ChildKiller + Send + Sync>,
}

#[derive(Default)]
pub struct Ptys(Mutex<HashMap<String, Session>>);

impl Ptys {
    pub fn kill_all(&self) {
        for (_, mut s) in self.0.lock().unwrap().drain() {
            let _ = s.killer.kill();
        }
    }
}

#[derive(Clone, Serialize)]
struct Data<'a> {
    id: &'a str,
    data: String,
}

#[derive(Clone, Serialize)]
struct Exit<'a> {
    id: &'a str,
    code: u32,
}

fn size(cols: u16, rows: u16) -> PtySize {
    PtySize { rows: rows.max(2), cols: cols.max(10), pixel_width: 0, pixel_height: 0 }
}

/// Starts `program args…` in a new pseudo-terminal. With no program, starts
/// the user's shell (PowerShell on Windows).
#[tauri::command]
pub fn pty_spawn(
    app: AppHandle,
    id: String,
    program: Option<String>,
    args: Option<Vec<String>>,
    cwd: Option<String>,
    cols: u16,
    rows: u16,
) -> Res<()> {
    let pair = native_pty_system().openpty(size(cols, rows)).map_err(err)?;
    let mut cmd = match program {
        Some(p) => CommandBuilder::new(p),
        None if cfg!(windows) => {
            let mut c = CommandBuilder::new("powershell.exe");
            c.arg("-NoLogo");
            c
        }
        None => CommandBuilder::new(std::env::var("SHELL").unwrap_or_else(|_| "sh".into())),
    };
    cmd.args(args.unwrap_or_default());
    if let Some(dir) = cwd.filter(|d| std::path::Path::new(d).is_dir()) {
        cmd.cwd(dir);
    }
    let mut child = pair.slave.spawn_command(cmd).map_err(err)?;
    drop(pair.slave);
    let mut reader = pair.master.try_clone_reader().map_err(err)?;
    let session = Session {
        writer: pair.master.take_writer().map_err(err)?,
        killer: child.clone_killer(),
        master: pair.master,
    };
    app.state::<State>().ptys.0.lock().unwrap().insert(id.clone(), session);

    let (out_app, out_id) = (app.clone(), id.clone());
    std::thread::spawn(move || {
        let mut buf = [0u8; 16 * 1024];
        while let Ok(n) = reader.read(&mut buf) {
            if n == 0 {
                break;
            }
            let data = base64::engine::general_purpose::STANDARD.encode(&buf[..n]);
            let _ = out_app.emit("pty:data", Data { id: &out_id, data });
        }
    });
    std::thread::spawn(move || {
        let code = child.wait().map(|s| s.exit_code()).unwrap_or(1);
        // Let the last output drain, then close the console so the reader ends.
        std::thread::sleep(std::time::Duration::from_millis(150));
        app.state::<State>().ptys.0.lock().unwrap().remove(&id);
        let _ = app.emit("pty:exit", Exit { id: &id, code });
    });
    Ok(())
}

#[tauri::command]
pub fn pty_write(state: tauri::State<State>, id: String, data: String) -> Res<()> {
    let mut map = state.ptys.0.lock().unwrap();
    let s = map.get_mut(&id).ok_or("terminal has exited")?;
    s.writer.write_all(data.as_bytes()).and_then(|_| s.writer.flush()).map_err(err)
}

#[tauri::command]
pub fn pty_resize(state: tauri::State<State>, id: String, cols: u16, rows: u16) -> Res<()> {
    match state.ptys.0.lock().unwrap().get(&id) {
        Some(s) => s.master.resize(size(cols, rows)).map_err(err),
        None => Ok(()),
    }
}

#[tauri::command]
pub fn pty_kill(state: tauri::State<State>, id: String) -> Res<()> {
    if let Some(s) = state.ptys.0.lock().unwrap().get_mut(&id) {
        s.killer.kill().map_err(err)?;
    }
    Ok(())
}
