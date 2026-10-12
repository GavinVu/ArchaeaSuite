// Typed wrappers around the backend commands (src-tauri/src/*.rs).
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

export interface Entry { name: string; path: string; dir: boolean }
export interface TextFile { text: string; mtime: number; eol: "\r\n" | "\n" }
export interface Stat { exists: boolean; dir: boolean; mtime: number; size: number }
export interface Query { text: string; regex: boolean; caseSensitive: boolean; wholeWord: boolean }
export interface Match { line: number; col: number; len: number; text: string }
export interface FileMatches { path: string; matches: Match[] }
export interface ExecOutput { code: number; stdout: string; stderr: string }
export interface AppInfo { version: string; arch: string; dataDir: string; home: string; debug: boolean }

export const api = {
  appInfo: () => invoke<AppInfo>("app_info"),
  startupPath: () => invoke<string | null>("startup_path"),
  loadConfig: (name: string) => invoke<string | null>("load_config", { name }),
  saveConfig: (name: string, text: string) => invoke<void>("save_config", { name, text }),

  listDir: (path: string) => invoke<Entry[]>("list_dir", { path }),
  readText: (path: string) => invoke<TextFile>("read_text", { path }),
  writeText: (path: string, text: string) => invoke<number>("write_text", { path, text }),
  createFile: (path: string) => invoke<void>("create_file", { path }),
  createDir: (path: string) => invoke<void>("create_dir", { path }),
  rename: (from: string, to: string) => invoke<void>("rename_path", { from, to }),
  trash: (path: string) => invoke<void>("trash_path", { path }),
  stat: (path: string) => invoke<Stat>("stat_path", { path }),

  listFiles: (root: string) => invoke<string[]>("list_files", { root }),
  search: (root: string, query: Query) =>
    invoke<{ files: FileMatches[]; truncated: boolean }>("search", { root, query }),
  replaceAll: (root: string, query: Query, replacement: string, only?: string[]) =>
    invoke<number>("replace_all", { root, query, replacement, only }),

  ptySpawn: (id: string, o: { program?: string; args?: string[]; cwd?: string | null; cols: number; rows: number }) =>
    invoke<void>("pty_spawn", { id, ...o }),
  ptyWrite: (id: string, data: string) => invoke<void>("pty_write", { id, data }),
  ptyResize: (id: string, cols: number, rows: number) => invoke<void>("pty_resize", { id, cols, rows }),
  ptyKill: (id: string) => invoke<void>("pty_kill", { id }),

  rpcStart: (id: string, program: string, args: string[], cwd?: string | null) =>
    invoke<void>("rpc_start", { id, program, args, cwd }),
  rpcSend: (id: string, message: string) => invoke<void>("rpc_send", { id, message }),
  rpcStop: (id: string) => invoke<void>("rpc_stop", { id }),

  exec: (program: string, args: string[], cwd?: string | null, stdin?: string, timeoutSecs?: number) =>
    invoke<ExecOutput>("exec", { program, args, cwd, stdin, timeoutSecs }),
  which: (program: string) => invoke<string | null>("which", { program }),

  aiModels: (baseUrl: string) => invoke<string[]>("ai_models", { baseUrl }),
  aiChat: (id: string, baseUrl: string, model: string, messages: { role: string; content: string }[]) =>
    invoke<void>("ai_chat", { id, baseUrl, model, messages }),
  aiStart: (program: string) => invoke<void>("ai_start", { program }),
  aiCancel: (id: string) => invoke<void>("ai_cancel", { id }),
};

/** Subscribes to a backend event for the lifetime of the app. */
export function on<T>(event: string, handler: (payload: T) => void) {
  void listen<T>(event, (e) => handler(e.payload));
}

// Path helpers. Paths are Windows paths as the backend returns them.
export const sep = "\\";
export const join = (...parts: string[]) => parts.join(sep).replace(/[\\/]+/g, sep);
export const basename = (p: string) => p.replace(/[\\/]+$/, "").split(/[\\/]/).pop() ?? p;
export const dirname = (p: string) => p.replace(/[\\/][^\\/]*[\\/]?$/, "");
export const extname = (p: string) => {
  const name = basename(p);
  const dot = name.lastIndexOf(".");
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
};
export const samePath = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
export const relative = (root: string, p: string) =>
  p.toLowerCase().startsWith(root.toLowerCase() + sep) ? p.slice(root.length + 1) : p;
export const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));
