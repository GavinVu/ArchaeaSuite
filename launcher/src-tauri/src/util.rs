use crate::error::Result;
use serde::de::DeserializeOwned;
use serde::Serialize;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

pub fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

/// Short unique suffix for staging/backup directory names.
pub fn nonce() -> String {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    format!(
        "{:x}{:x}{:x}",
        nanos,
        std::process::id(),
        COUNTER.fetch_add(1, Ordering::Relaxed)
    )
}

pub fn read_json<T: DeserializeOwned>(path: &Path) -> Option<T> {
    let bytes = fs::read(path).ok()?;
    serde_json::from_slice(&bytes).ok()
}

/// Writes JSON to a sibling temp file and renames it into place, so a crash
/// mid-write can never leave a truncated settings or registry file.
pub fn write_json_atomic<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let tmp = path.with_extension(format!("tmp-{}", nonce()));
    fs::write(&tmp, serde_json::to_vec_pretty(value)?)?;
    if let Err(e) = fs::rename(&tmp, path) {
        let _ = fs::remove_file(&tmp);
        return Err(e.into());
    }
    Ok(())
}

pub fn dir_size(path: &Path) -> u64 {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return 0;
    };
    if meta.is_file() {
        return meta.len();
    }
    if !meta.is_dir() {
        return 0;
    }
    let Ok(entries) = fs::read_dir(path) else {
        return 0;
    };
    entries.flatten().map(|e| dir_size(&e.path())).sum()
}

pub fn format_bytes(bytes: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 {
        format!("{bytes} B")
    } else {
        format!("{value:.1} {}", UNITS[unit])
    }
}

const RESERVED_NAMES: [&str; 22] = [
    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
    "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
];

/// Converts an untrusted relative path (from the catalog or a ZIP entry) into
/// a path that is guaranteed to stay inside whatever directory it is joined
/// to. Rejects absolute paths, drive letters, `..`, alternate data streams,
/// reserved device names and anything else Windows would reinterpret.
pub fn safe_relative_path(raw: &str) -> Option<PathBuf> {
    let normalized = raw.replace('\\', "/");
    let trimmed = normalized.strip_suffix('/').unwrap_or(&normalized);
    if trimmed.is_empty() || trimmed.starts_with('/') {
        return None;
    }
    let mut out = PathBuf::new();
    for part in trimmed.split('/') {
        if part.is_empty() || part == "." || part == ".." {
            return None;
        }
        if part.ends_with('.') || part.ends_with(' ') {
            return None;
        }
        if part
            .chars()
            .any(|c| c.is_control() || matches!(c, ':' | '<' | '>' | '"' | '|' | '?' | '*'))
        {
            return None;
        }
        let stem = part.split('.').next().unwrap_or(part);
        if RESERVED_NAMES.iter().any(|r| r.eq_ignore_ascii_case(stem)) {
            return None;
        }
        out.push(part);
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_ordinary_relative_paths() {
        assert_eq!(
            safe_relative_path("bin/App.exe"),
            Some(PathBuf::from("bin").join("App.exe"))
        );
        assert_eq!(safe_relative_path("data/"), Some(PathBuf::from("data")));
        assert_eq!(
            safe_relative_path("a\\b.txt"),
            Some(PathBuf::from("a").join("b.txt"))
        );
    }

    #[test]
    fn rejects_escapes_and_windows_tricks() {
        for bad in [
            "",
            "/abs",
            "\\abs",
            "../up",
            "a/../../b",
            "a/./b",
            "C:/Windows/x",
            "C:evil",
            "file.txt:stream",
            "a//b",
            "trailingdot.",
            "trailingspace ",
            "NUL",
            "con.txt",
            "dir/LPT1",
            "what?.txt",
        ] {
            assert_eq!(safe_relative_path(bad), None, "should reject {bad:?}");
        }
    }

    #[test]
    fn formats_bytes() {
        assert_eq!(format_bytes(512), "512 B");
        assert_eq!(format_bytes(1536), "1.5 KB");
        assert_eq!(format_bytes(5 * 1024 * 1024), "5.0 MB");
    }
}
