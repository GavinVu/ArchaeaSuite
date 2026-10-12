# ArchaeaIDE

A code editor and IDE for Windows 10/11 (x64 and ARM64), part of ArchaeaSuite.
Tauri 2 + Rust backend, React + TypeScript frontend, built on
[Monaco](https://github.com/microsoft/monaco-editor) (the editor inside VS Code)
and [xterm.js](https://github.com/xtermjs/xterm.js). It shares the launcher's
stylesheet, so the two always match.

## Features

- **Editor:** syntax highlighting for 80+ languages, autocomplete, minimap,
  multiple cursors, find/replace, formatting, tabs, up to three split editors,
  diff view. IntelliSense, diagnostics and formatting are built in for
  JavaScript, TypeScript, JSON, CSS and HTML.
- **Language servers:** other languages use a server found on PATH (pyright,
  clangd, gopls, rust-analyzer, csharp-ls, jdtls, …) for completion, hover,
  diagnostics, go to definition, references, rename, symbols and formatting.
- **Files:** explorer (create, rename, delete to the Recycle Bin), quick open
  (Ctrl+P), search and replace across the folder with regex, session restore.
- **Terminal:** real pseudo-terminals (ConPTY); as many as you like.
- **Run / Stop:** Python, JavaScript, TypeScript, C, C++, C#, Java, Go, Rust,
  Ruby, PHP, Lua, Perl, R, Dart, PowerShell, batch and shell scripts — using
  the compilers and runtimes you have installed. Add your own under
  `"runners"` in settings.json.
- **Debugging:** breakpoints, call stack, variables, stepping and a debug
  console for Python (debugpy) and JavaScript/TypeScript (Node.js inspector).
- **Git:** status, stage/unstage, discard, commit, branches, pull/push, diffs
  against HEAD, recent commits.
- **More:** command palette, project templates, tasks (`.archaea/tasks.json`
  plus detected npm/cargo/dotnet/go/make tasks), customisable shortcuts,
  sandboxed extensions.
- **Local AI (optional):** chat about the open file or selection with a model
  running on your own PC through [Ollama](https://ollama.com). The panel walks
  through setup: install Ollama (winget), start it, download a model.

## Safety

Nothing runs code unless you ask: Run, Debug, a task, or a terminal command.
Opening a folder never executes anything in it. Language servers that only
analyse code start automatically (this can be turned off); ones that run
project build scripts (Rust, C#, Java) wait for a click in the status bar.
Extensions are off until switched on and run in a Web Worker with no access to
files, processes or the backend. The AI client refuses any address that is not
this computer.

## Known limits of this preview

- Debugging covers Python and Node.js only. Other languages can be run but not
  stepped through.
- One folder per window; no multi-root workspaces.
- No extension marketplace and no VS Code extension compatibility: extensions
  are local folders with a small API (commands, snippets, editor text).
- Shortcuts are single chords (no Ctrl+K Ctrl+S sequences).
- Git covers the everyday commands; merges, rebases and stashes go through
  the terminal.
- Language servers, compilers, debugpy and Ollama are not bundled.
- Local AI speaks the Ollama HTTP API only, and answers are as good as the
  model you download; the small default is fast rather than clever.

## Develop

```powershell
cd apps/ArchaeaIDE
npm install
npm run tauri dev          # run
npm run typecheck
cd src-tauri; cargo test   # backend tests
./build.ps1 [-Arch arm64] [-Version x.y.z]   # release exe + launcher package → /packages
```

Settings, shortcuts, session and extensions live in
`%APPDATA%\ArchaeaSuite\ArchaeaIDE` (`ARCHAEAIDE_DATA_DIR` overrides).
Development builds expose `window.ide` so the UI can be scripted over the
WebView2 debugging port.
