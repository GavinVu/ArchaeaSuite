use crate::{err, Res};
use serde::Serialize;
use std::fs;
use std::path::Path;
use std::time::UNIX_EPOCH;

/// Files larger than this are not opened as text.
const MAX_TEXT: u64 = 24 * 1024 * 1024;

#[derive(Serialize)]
pub struct Entry {
    name: String,
    path: String,
    dir: bool,
}

#[derive(Serialize)]
pub struct TextFile {
    text: String,
    mtime: u64,
    /// "\r\n" or "\n", so saving keeps the file's line endings.
    eol: &'static str,
}

#[derive(Serialize)]
pub struct Stat {
    exists: bool,
    dir: bool,
    mtime: u64,
    size: u64,
}

fn mtime(path: &Path) -> u64 {
    fs::metadata(path)
        .and_then(|m| m.modified())
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

#[tauri::command]
pub fn list_dir(path: String) -> Res<Vec<Entry>> {
    let mut out: Vec<Entry> = fs::read_dir(&path)
        .map_err(err)?
        .filter_map(|e| e.ok())
        .map(|e| Entry {
            name: e.file_name().to_string_lossy().into_owned(),
            path: e.path().to_string_lossy().into_owned(),
            dir: e.file_type().map(|t| t.is_dir()).unwrap_or(false),
        })
        .collect();
    out.sort_by(|a, b| b.dir.cmp(&a.dir).then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase())));
    Ok(out)
}

pub fn decode(bytes: Vec<u8>) -> Res<String> {
    let bytes = match bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
        Some(rest) => rest.to_vec(),
        None => bytes,
    };
    String::from_utf8(bytes).map_err(|_| "This file is not UTF-8 text (it may be binary).".to_string())
}

#[tauri::command]
pub fn read_text(path: String) -> Res<TextFile> {
    let p = Path::new(&path);
    let size = fs::metadata(p).map_err(err)?.len();
    if size > MAX_TEXT {
        return Err(format!("File is too large to open ({} MB).", size / 1024 / 1024));
    }
    let text = decode(fs::read(p).map_err(err)?)?;
    let eol = if text.contains("\r\n") { "\r\n" } else { "\n" };
    Ok(TextFile { text, mtime: mtime(p), eol })
}

#[tauri::command]
pub fn write_text(path: String, text: String) -> Res<u64> {
    let p = Path::new(&path);
    if let Some(parent) = p.parent() {
        fs::create_dir_all(parent).map_err(err)?;
    }
    fs::write(p, text).map_err(err)?;
    Ok(mtime(p))
}

#[tauri::command]
pub fn create_file(path: String) -> Res<()> {
    let p = Path::new(&path);
    if p.exists() {
        return Err(format!("{} already exists.", p.display()));
    }
    if let Some(parent) = p.parent() {
        fs::create_dir_all(parent).map_err(err)?;
    }
    fs::write(p, "").map_err(err)
}

#[tauri::command]
pub fn create_dir(path: String) -> Res<()> {
    if Path::new(&path).exists() {
        return Err(format!("{path} already exists."));
    }
    fs::create_dir_all(&path).map_err(err)
}

#[tauri::command]
pub fn rename_path(from: String, to: String) -> Res<()> {
    // A case-only rename is the same file on Windows and must be allowed.
    if Path::new(&to).exists() && from.to_lowercase() != to.to_lowercase() {
        return Err(format!("{to} already exists."));
    }
    fs::rename(&from, &to).map_err(err)
}

/// Deleting from the explorer goes to the Recycle Bin, never straight to nothing.
#[tauri::command]
pub fn trash_path(path: String) -> Res<()> {
    trash::delete(&path).map_err(err)
}

#[tauri::command]
pub fn stat_path(path: String) -> Stat {
    let p = Path::new(&path);
    match fs::metadata(p) {
        Ok(m) => Stat { exists: true, dir: m.is_dir(), mtime: mtime(p), size: m.len() },
        Err(_) => Stat { exists: false, dir: false, mtime: 0, size: 0 },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_text_and_reports_line_endings() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("sub").join("a.txt").to_string_lossy().into_owned();
        write_text(file.clone(), "one\r\ntwo".into()).unwrap();
        let read = read_text(file.clone()).unwrap();
        assert_eq!(read.text, "one\r\ntwo");
        assert_eq!(read.eol, "\r\n");
        assert!(create_file(file).is_err(), "must not overwrite an existing file");
    }

    #[test]
    fn strips_bom_and_rejects_binary() {
        assert_eq!(decode(vec![0xEF, 0xBB, 0xBF, b'h', b'i']).unwrap(), "hi");
        assert!(decode(vec![0xFF, 0xFE, 0x00]).is_err());
    }

    #[test]
    fn lists_folders_first_and_refuses_to_clobber_on_rename() {
        let dir = tempfile::tempdir().unwrap();
        let p = |n: &str| dir.path().join(n).to_string_lossy().into_owned();
        create_file(p("b.txt")).unwrap();
        create_file(p("a.txt")).unwrap();
        create_dir(p("zdir")).unwrap();
        let names: Vec<_> = list_dir(p("")).unwrap().into_iter().map(|e| e.name).collect();
        assert_eq!(names, ["zdir", "a.txt", "b.txt"]);
        assert!(rename_path(p("a.txt"), p("b.txt")).is_err());
        rename_path(p("a.txt"), p("c.txt")).unwrap();
        assert!(stat_path(p("c.txt")).exists && !stat_path(p("a.txt")).exists);
    }
}
