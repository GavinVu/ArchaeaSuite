// One store for the whole UI, plus the settings and session files behind it.
import { useSyncExternalStore } from "react";
import { api, errorText, type AppInfo } from "./api";

export type TabKind = "file" | "diff" | "settings" | "keys" | "welcome";
export interface Tab { id: string; kind: TabKind; path?: string; title: string }
export interface Group { id: number; tabs: Tab[]; active: string | null }
export type SideView = "explorer" | "search" | "git" | "debug" | "extensions" | "ai";
export type PanelView = "terminal" | "problems" | "output" | "console";
export interface Toast { id: number; tone: "info" | "ok" | "error"; title: string; detail?: string }
export interface TermInfo { id: string; title: string; kind: "shell" | "run" | "task"; alive: boolean }

export interface Settings {
  theme: "dark" | "light";
  fontSize: number;
  tabSize: number;
  wordWrap: boolean;
  minimap: boolean;
  formatOnSave: boolean;
  autoSave: boolean;
  /** Program for new terminals; empty means Windows PowerShell. */
  shell: string;
  /** Python interpreter for Run and Debug (a virtual environment's python.exe, say); empty uses PATH. */
  python: string;
  /** Start language servers found on PATH when a matching file is opened. */
  languageServers: boolean;
  /** Language id -> command line, overriding the built-in server list. */
  serverCommands: Record<string, string>;
  /** File extension -> command line; ${file} is replaced with the file path. */
  runners: Record<string, string>;
  aiEnabled: boolean;
  aiUrl: string;
  aiModel: string;
  /** Ids of extensions the user has switched on. */
  extensions: string[];
}

export const defaultSettings: Settings = {
  theme: "dark",
  fontSize: 14,
  tabSize: 4,
  wordWrap: false,
  minimap: true,
  formatOnSave: false,
  autoSave: false,
  shell: "",
  python: "",
  languageServers: true,
  serverCommands: {},
  runners: {},
  aiEnabled: false,
  aiUrl: "http://127.0.0.1:11434",
  aiModel: "",
  extensions: [],
};

export interface State {
  info: AppInfo | null;
  root: string | null;
  recent: string[];
  groups: Group[];
  activeGroup: number;
  dirty: Record<string, true>;
  side: SideView;
  sideOpen: boolean;
  sideWidth: number;
  panel: PanelView;
  panelOpen: boolean;
  panelHeight: number;
  settings: Settings;
  keys: Record<string, string>;
  toasts: Toast[];
  terminals: TermInfo[];
  activeTerminal: string | null;
  running: boolean;
  /** Bumped whenever files on disk may have changed. */
  fsVersion: number;
  cursor: { line: number; col: number; selected: number } | null;
  language: string;
  breakpoints: Record<string, number[]>;
  output: string;
}

let state: State = {
  info: null,
  root: null,
  recent: [],
  groups: [{ id: 1, tabs: [], active: null }],
  activeGroup: 1,
  dirty: {},
  side: "explorer",
  sideOpen: true,
  sideWidth: 270,
  panel: "terminal",
  panelOpen: false,
  panelHeight: 260,
  settings: defaultSettings,
  keys: {},
  toasts: [],
  terminals: [],
  activeTerminal: null,
  running: false,
  fsVersion: 0,
  cursor: null,
  language: "",
  breakpoints: {},
  output: "",
};

const listeners = new Set<() => void>();
export const get = () => state;
export function set(patch: Partial<State> | ((s: State) => Partial<State>)) {
  state = { ...state, ...(typeof patch === "function" ? patch(state) : patch) };
  listeners.forEach((l) => l());
}
const subscribe = (l: () => void) => (listeners.add(l), () => listeners.delete(l));
export function useStore<T>(selector: (s: State) => T): T {
  return useSyncExternalStore(subscribe, () => selector(state));
}
export const onChange = subscribe;

// ---------------------------------------------------------------- feedback

let toastId = 0;
export function toast(tone: Toast["tone"], title: string, detail?: string) {
  const id = ++toastId;
  set((s) => ({ toasts: [...s.toasts.slice(-3), { id, tone, title, detail }] }));
  if (tone !== "error") setTimeout(() => dismissToast(id), 4500);
}
export const dismissToast = (id: number) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) }));
export const fail = (title: string) => (e: unknown) => toast("error", title, errorText(e));

export function log(text: string) {
  set((s) => ({ output: (s.output + text).slice(-200_000) }));
}

export const touchFs = () => set((s) => ({ fsVersion: s.fsVersion + 1 }));

// ---------------------------------------------------------- settings files

export function applyTheme() {
  document.documentElement.dataset.theme = state.settings.theme;
}

export function saveSettings(patch: Partial<Settings>) {
  set((s) => ({ settings: { ...s.settings, ...patch } }));
  applyTheme();
  api.saveConfig("settings.json", JSON.stringify(state.settings, null, 2)).catch(fail("Could not save settings"));
}

export function saveKeys(keys: Record<string, string>) {
  set({ keys });
  api.saveConfig("keybindings.json", JSON.stringify(keys, null, 2)).catch(fail("Could not save shortcuts"));
}

const parse = <T,>(text: string | null, fallback: T): T => {
  try {
    return text ? (JSON.parse(text) as T) : fallback;
  } catch {
    return fallback;
  }
};

interface Session {
  root: string | null;
  recent: string[];
  files: string[];
  active: string | null;
  sideWidth: number;
  panelHeight: number;
  breakpoints: Record<string, number[]>;
}

export async function loadConfig(): Promise<Session> {
  const [info, settings, keys, session] = await Promise.all([
    api.appInfo(),
    api.loadConfig("settings.json"),
    api.loadConfig("keybindings.json"),
    api.loadConfig("state.json"),
  ]);
  const saved = parse<Partial<Session>>(session, {});
  set({
    info,
    settings: { ...defaultSettings, ...parse<Partial<Settings>>(settings, {}) },
    keys: parse(keys, {}),
    recent: saved.recent ?? [],
    sideWidth: saved.sideWidth ?? 270,
    panelHeight: saved.panelHeight ?? 260,
    breakpoints: saved.breakpoints ?? {},
  });
  applyTheme();
  return { root: saved.root ?? null, recent: saved.recent ?? [], files: saved.files ?? [], active: saved.active ?? null,
    sideWidth: 0, panelHeight: 0, breakpoints: {} };
}

/** Re-reads settings.json and keybindings.json after they were edited as text. */
export async function reloadConfig() {
  const [settings, keys] = await Promise.all([api.loadConfig("settings.json"), api.loadConfig("keybindings.json")]);
  set({ settings: { ...defaultSettings, ...parse<Partial<Settings>>(settings, {}) }, keys: parse(keys, {}) });
  applyTheme();
}

let sessionTimer = 0;
let frozen = false;
function writeSession() {
  const s = state;
  const group = s.groups.find((g) => g.id === s.activeGroup) ?? s.groups[0];
  const session: Session = {
    root: s.root,
    recent: s.recent,
    files: s.groups.flatMap((g) => g.tabs).filter((t) => t.kind === "file").map((t) => t.path!),
    active: group.tabs.find((t) => t.id === group.active && t.kind === "file")?.path ?? null,
    sideWidth: s.sideWidth,
    panelHeight: s.panelHeight,
    breakpoints: s.breakpoints,
  };
  return api.saveConfig("state.json", JSON.stringify(session)).catch(() => {});
}

export function saveSession() {
  if (frozen) return;
  clearTimeout(sessionTimer);
  sessionTimer = window.setTimeout(() => void writeSession(), 400);
}

/** Writes the session now and ignores later changes, so closing tabs on exit does not forget them. */
export async function freezeSession(on: boolean) {
  clearTimeout(sessionTimer);
  frozen = false;
  if (on) await writeSession();
  else saveSession();
  frozen = on;
}
