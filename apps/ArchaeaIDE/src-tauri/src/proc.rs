//! One-shot commands (git, formatters, tool detection). No shell is involved:
//! the program is resolved on PATH and started with an argument list.

use crate::{err, Res};
use serde::Serialize;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

#[derive(Serialize)]
pub struct Output {
    code: i32,
    stdout: String,
    stderr: String,
}

/// Finds a program the way the shell would: as given if it is a path, else in
/// each PATH folder with each PATHEXT extension.
pub fn resolve(program: &str) -> Option<PathBuf> {
    let exts: Vec<String> = if cfg!(windows) {
        std::env::var("PATHEXT")
            .unwrap_or_else(|_| ".COM;.EXE;.BAT;.CMD".into())
            .split(';')
            .map(|e| e.to_lowercase())
            .collect()
    } else {
        vec![]
    };
    let candidates = |base: &Path| -> Option<PathBuf> {
        if base.extension().is_some() && base.is_file() {
            return Some(base.to_path_buf());
        }
        for ext in &exts {
            let mut name = base.as_os_str().to_owned();
            name.push(ext);
            let p = PathBuf::from(name);
            if p.is_file() {
                return Some(p);
            }
        }
        (!cfg!(windows) && base.is_file()).then(|| base.to_path_buf())
    };
    if program.contains('/') || program.contains('\\') {
        return candidates(Path::new(program));
    }
    std::env::split_paths(&std::env::var_os("PATH")?).find_map(|dir| candidates(&dir.join(program)))
}

pub fn command(program: &str, args: &[String], cwd: Option<&str>) -> Res<Command> {
    let path = resolve(program).ok_or_else(|| format!("{program} was not found on PATH."))?;
    let mut cmd = Command::new(path);
    cmd.args(args);
    if let Some(dir) = cwd.filter(|d| Path::new(d).is_dir()) {
        cmd.current_dir(dir);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    Ok(cmd)
}

#[tauri::command]
pub fn which(program: String) -> Option<String> {
    resolve(&program).map(|p| p.to_string_lossy().into_owned())
}

#[tauri::command(async)]
pub fn exec(
    program: String,
    args: Vec<String>,
    cwd: Option<String>,
    stdin: Option<String>,
    timeout_secs: Option<u64>,
) -> Res<Output> {
    let mut cmd = command(&program, &args, cwd.as_deref())?;
    // Never let git stop to ask for a password on a console nobody can see.
    cmd.env("GIT_TERMINAL_PROMPT", "0");
    let mut child = cmd
        .stdin(if stdin.is_some() { Stdio::piped() } else { Stdio::null() })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(err)?;
    if let (Some(text), Some(mut pipe)) = (stdin, child.stdin.take()) {
        std::thread::spawn(move || {
            let _ = pipe.write_all(text.as_bytes());
        });
    }
    let drain = |mut pipe: Box<dyn Read + Send>| {
        std::thread::spawn(move || {
            let mut bytes = Vec::new();
            let _ = pipe.read_to_end(&mut bytes);
            String::from_utf8_lossy(&bytes).into_owned()
        })
    };
    let out = drain(Box::new(child.stdout.take().ok_or("no stdout")?));
    let errs = drain(Box::new(child.stderr.take().ok_or("no stderr")?));
    let deadline = Instant::now() + Duration::from_secs(timeout_secs.unwrap_or(60));
    let status = loop {
        if let Some(status) = child.try_wait().map_err(err)? {
            break status;
        }
        if Instant::now() > deadline {
            let _ = child.kill();
            let _ = child.wait();
            return Err(format!("{program} timed out."));
        }
        std::thread::sleep(Duration::from_millis(15));
    };
    Ok(Output {
        code: status.code().unwrap_or(-1),
        stdout: out.join().unwrap_or_default(),
        stderr: errs.join().unwrap_or_default(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[test]
    fn resolves_on_path_and_passes_stdin_and_exit_code() {
        assert!(resolve("cmd").is_some());
        assert!(resolve("surely-not-a-real-program-1234").is_none());
        let out = exec("findstr".into(), vec!["b".into()], None, Some("a\nb\n".into()), None).unwrap();
        assert_eq!((out.code, out.stdout.trim()), (0, "b"));
        let out = exec("cmd".into(), vec!["/c".into(), "exit 3".into()], None, None, None).unwrap();
        assert_eq!(out.code, 3);
    }
}
