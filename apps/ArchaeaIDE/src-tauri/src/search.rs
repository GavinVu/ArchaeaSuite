//! Workspace file listing, search and replace. Walks with the `ignore` crate,
//! so `.gitignore` is respected and `.git` is never entered.

use crate::{err, files::decode, Res};
use ignore::WalkBuilder;
use regex::{Regex, RegexBuilder};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};

const MAX_FILES: usize = 40_000;
const MAX_MATCHES: usize = 5_000;
const MAX_FILE_SIZE: u64 = 4 * 1024 * 1024;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Query {
    text: String,
    #[serde(default)]
    regex: bool,
    #[serde(default)]
    case_sensitive: bool,
    #[serde(default)]
    whole_word: bool,
}

#[derive(Serialize)]
pub struct Match {
    line: usize,
    col: usize,
    len: usize,
    text: String,
}

#[derive(Serialize)]
pub struct FileMatches {
    path: String,
    matches: Vec<Match>,
}

#[derive(Serialize)]
pub struct SearchResult {
    files: Vec<FileMatches>,
    truncated: bool,
}

fn walk(root: &str) -> impl Iterator<Item = PathBuf> {
    WalkBuilder::new(root)
        .hidden(false)
        .filter_entry(|e| e.file_name() != ".git")
        .build()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().map(|t| t.is_file()).unwrap_or(false))
        .map(|e| e.into_path())
}

fn compile(q: &Query) -> Res<Regex> {
    if q.text.is_empty() {
        return Err("Type something to search for.".into());
    }
    let mut pattern = if q.regex { q.text.clone() } else { regex::escape(&q.text) };
    if q.whole_word {
        pattern = format!(r"\b(?:{pattern})\b");
    }
    RegexBuilder::new(&pattern).case_insensitive(!q.case_sensitive).build().map_err(err)
}

/// Text of a searchable file: small enough, UTF-8 and without NUL bytes.
fn searchable(path: &Path) -> Option<String> {
    if fs::metadata(path).ok()?.len() > MAX_FILE_SIZE {
        return None;
    }
    let bytes = fs::read(path).ok()?;
    if bytes.contains(&0) {
        return None;
    }
    decode(bytes).ok()
}

/// Paths relative to `root`, forward slashes, for quick open.
#[tauri::command(async)]
pub fn list_files(root: String) -> Vec<String> {
    walk(&root)
        .take(MAX_FILES)
        .filter_map(|p| p.strip_prefix(&root).ok().map(|r| r.to_string_lossy().replace('\\', "/")))
        .collect()
}

#[tauri::command(async)]
pub fn search(root: String, query: Query) -> Res<SearchResult> {
    let re = compile(&query)?;
    let mut files = Vec::new();
    let mut total = 0;
    for path in walk(&root) {
        let Some(text) = searchable(&path) else { continue };
        let mut matches = Vec::new();
        for (i, line) in text.lines().enumerate() {
            for m in re.find_iter(line) {
                if m.is_empty() {
                    continue;
                }
                // Columns are UTF-16 units, which is what the editor counts in.
                let col = line[..m.start()].encode_utf16().count();
                let len = m.as_str().encode_utf16().count();
                let shown: String = line.chars().take(400).collect();
                matches.push(Match { line: i + 1, col: col + 1, len, text: shown });
                total += 1;
            }
            if total >= MAX_MATCHES {
                break;
            }
        }
        if !matches.is_empty() {
            files.push(FileMatches { path: path.to_string_lossy().into_owned(), matches });
        }
        if total >= MAX_MATCHES {
            return Ok(SearchResult { files, truncated: true });
        }
    }
    Ok(SearchResult { files, truncated: false })
}

/// Replaces every match in the workspace (or only in `only`, when given).
/// Returns the number of replacements. In regex mode `$1` refers to a group.
#[tauri::command(async)]
pub fn replace_all(root: String, query: Query, replacement: String, only: Option<Vec<String>>) -> Res<usize> {
    let re = compile(&query)?;
    let paths: Vec<PathBuf> = match only {
        Some(list) => list.into_iter().map(PathBuf::from).collect(),
        None => walk(&root).collect(),
    };
    let mut count = 0;
    for path in paths {
        let Some(text) = searchable(&path) else { continue };
        let found = re.find_iter(&text).filter(|m| !m.is_empty()).count();
        if found == 0 {
            continue;
        }
        let replaced = if query.regex {
            re.replace_all(&text, replacement.as_str())
        } else {
            re.replace_all(&text, regex::NoExpand(&replacement))
        };
        fs::write(&path, replaced.as_bytes()).map_err(err)?;
        count += found;
    }
    Ok(count)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn q(text: &str, regex: bool, whole_word: bool) -> Query {
        Query { text: text.into(), regex, case_sensitive: false, whole_word }
    }

    fn workspace() -> tempfile::TempDir {
        let dir = tempfile::tempdir().unwrap();
        fs::create_dir_all(dir.path().join(".git")).unwrap();
        fs::write(dir.path().join(".git/config"), "needle").unwrap();
        fs::write(dir.path().join(".gitignore"), "ignored/\n").unwrap();
        fs::create_dir_all(dir.path().join("ignored")).unwrap();
        fs::write(dir.path().join("ignored/x.txt"), "needle").unwrap();
        fs::write(dir.path().join("a.txt"), "é Needle here\nneedles\n").unwrap();
        fs::write(dir.path().join("bin.dat"), b"needle\0").unwrap();
        dir
    }

    #[test]
    fn search_skips_git_ignored_and_binary_and_counts_utf16_columns() {
        let dir = workspace();
        let root = dir.path().to_string_lossy().into_owned();
        let found = search(root.clone(), q("needle", false, false)).unwrap();
        assert_eq!(found.files.len(), 1);
        let m = &found.files[0].matches;
        assert_eq!((m.len(), m[0].line, m[0].col, m[0].len), (2, 1, 3, 6));
        assert_eq!(search(root.clone(), q("needle", false, true)).unwrap().files[0].matches.len(), 1);
        let listed = list_files(root);
        assert!(listed.contains(&"a.txt".to_string()) && !listed.iter().any(|f| f.starts_with("ignored") || f.starts_with(".git/")));
    }

    #[test]
    fn replace_is_literal_unless_regex() {
        let dir = workspace();
        let root = dir.path().to_string_lossy().into_owned();
        assert_eq!(replace_all(root.clone(), q("needle", false, false), "$1x".into(), None).unwrap(), 2);
        assert_eq!(fs::read_to_string(dir.path().join("a.txt")).unwrap(), "é $1x here\n$1xs\n");
        assert_eq!(replace_all(root, q(r"(\w+) here", true, false), "<$1>".into(), None).unwrap(), 1);
        assert_eq!(fs::read_to_string(dir.path().join("a.txt")).unwrap(), "é $<1x>
$1xs
");
        assert_eq!(fs::read_to_string(dir.path().join("ignored/x.txt")).unwrap(), "needle");
    }
}
