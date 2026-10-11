//! Child processes that speak JSON messages framed with `Content-Length`
//! headers over stdio: language servers (LSP) and debug adapters (DAP).
//! The backend only frames and relays; the protocols live in the frontend.

use crate::{err, proc, Res, State};
use serde::Serialize;
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdin, Stdio};
use std::sync::Mutex;
use tauri::{AppHandle, Emitter, Manager};

struct Session {
    child: Child,
    stdin: ChildStdin,
}

#[derive(Default)]
pub struct Rpcs(Mutex<HashMap<String, Session>>);

impl Rpcs {
    pub fn kill_all(&self) {
        for (_, mut s) in self.0.lock().unwrap().drain() {
            let _ = s.child.kill();
        }
    }
}

#[derive(Clone, Serialize)]
struct Event<'a> {
    id: &'a str,
    text: String,
}

/// Reads one framed message. `Ok(None)` is a clean end of stream.
pub fn read_message(reader: &mut impl BufRead) -> std::io::Result<Option<String>> {
    let mut length = None;
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line)? == 0 {
            return Ok(None);
        }
        let line = line.trim_end();
        if line.is_empty() {
            if length.is_some() {
                break;
            }
            continue;
        }
        if let Some((name, value)) = line.split_once(':') {
            if name.eq_ignore_ascii_case("content-length") {
                length = value.trim().parse::<usize>().ok();
            }
        }
    }
    let mut body = vec![0u8; length.unwrap_or(0)];
    reader.read_exact(&mut body)?;
    Ok(Some(String::from_utf8_lossy(&body).into_owned()))
}

pub fn frame(message: &str) -> Vec<u8> {
    format!("Content-Length: {}\r\n\r\n{}", message.len(), message).into_bytes()
}

#[tauri::command]
pub fn rpc_start(app: AppHandle, id: String, program: String, args: Vec<String>, cwd: Option<String>) -> Res<()> {
    let mut cmd = proc::command(&program, &args, cwd.as_deref())?;
    let mut child = cmd
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("Could not start {program}: {e}"))?;
    let stdin = child.stdin.take().ok_or("no stdin")?;
    let stdout = child.stdout.take().ok_or("no stdout")?;
    let mut stderr = child.stderr.take().ok_or("no stderr")?;
    if let Some(mut old) = app.state::<State>().rpcs.0.lock().unwrap().insert(id.clone(), Session { child, stdin }) {
        let _ = old.child.kill();
    }

    let (log_app, log_id) = (app.clone(), id.clone());
    std::thread::spawn(move || {
        let mut buf = [0u8; 4096];
        while let Ok(n) = stderr.read(&mut buf) {
            if n == 0 {
                break;
            }
            let text = String::from_utf8_lossy(&buf[..n]).into_owned();
            let _ = log_app.emit("rpc:log", Event { id: &log_id, text });
        }
    });
    std::thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        while let Ok(Some(text)) = read_message(&mut reader) {
            let _ = app.emit("rpc:msg", Event { id: &id, text });
        }
        if let Some(mut s) = app.state::<State>().rpcs.0.lock().unwrap().remove(&id) {
            let _ = s.child.kill();
            let _ = s.child.wait();
        }
        let _ = app.emit("rpc:exit", Event { id: &id, text: String::new() });
    });
    Ok(())
}

#[tauri::command]
pub fn rpc_send(state: tauri::State<State>, id: String, message: String) -> Res<()> {
    let mut map = state.rpcs.0.lock().unwrap();
    let s = map.get_mut(&id).ok_or("process is not running")?;
    s.stdin.write_all(&frame(&message)).and_then(|_| s.stdin.flush()).map_err(err)
}

#[tauri::command]
pub fn rpc_stop(state: tauri::State<State>, id: String) {
    if let Some(s) = state.rpcs.0.lock().unwrap().get_mut(&id) {
        let _ = s.child.kill();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames_round_trip_and_count_bytes_not_chars() {
        let mut wire = frame(r#"{"a":"é"}"#);
        wire.extend(b"Content-Type: x\r\ncontent-length: 2\r\n\r\n{}");
        let mut reader = BufReader::new(&wire[..]);
        assert_eq!(read_message(&mut reader).unwrap().as_deref(), Some(r#"{"a":"é"}"#));
        assert_eq!(read_message(&mut reader).unwrap().as_deref(), Some("{}"));
        assert_eq!(read_message(&mut reader).unwrap(), None);
    }
}
