import { useMemo, useSyncExternalStore } from "react";
import { listen } from "@tauri-apps/api/event";
import {
  isPermissionGranted,
  requestPermission,
  sendNotification,
} from "@tauri-apps/plugin-notification";
import { check, type Update } from "@tauri-apps/plugin-updater";
import { relaunch } from "@tauri-apps/plugin-process";
import { api, toError } from "./api";
import { compareVersions } from "./format";
import type {
  AppEntry,
  Arch,
  Catalog,
  CatalogApp,
  EntryState,
  InstalledApp,
  Op,
  OpKind,
  Package,
  Page,
  Progress,
  Settings,
  SystemInfo,
  Theme,
} from "./types";

export interface Toast {
  id: number;
  tone: "info" | "success" | "error";
  title: string;
  detail?: string;
}

export interface LauncherUpdate {
  status: "idle" | "checking" | "up-to-date" | "available" | "installing" | "error";
  version?: string;
  notes?: string;
  done?: number;
  total?: number;
  error?: string;
}

interface State {
  ready: boolean;
  page: Page;
  selectedId: string | null;
  /** App awaiting an uninstall confirmation. */
  confirmUninstallId: string | null;
  system: SystemInfo | null;
  settings: Settings | null;
  catalog: Catalog | null;
  catalogSource: "network" | "cache" | "none";
  catalogFetchedAt: number | null;
  /** Why the last refresh failed; null when the last refresh succeeded. */
  catalogError: string | null;
  refreshing: boolean;
  installed: InstalledApp[];
  ops: Record<string, Op>;
  toasts: Toast[];
  launcher: LauncherUpdate;
}

let state: State = {
  ready: false,
  page: "home",
  selectedId: null,
  confirmUninstallId: null,
  system: null,
  settings: null,
  catalog: null,
  catalogSource: "none",
  catalogFetchedAt: null,
  catalogError: null,
  refreshing: false,
  installed: [],
  ops: {},
  toasts: [],
  launcher: { status: "idle" },
};

const listeners = new Set<() => void>();

function set(patch: Partial<State>) {
  state = { ...state, ...patch };
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Subscribe to one slice of the store. Select existing references or primitives. */
export function useStore<T>(selector: (s: State) => T): T {
  return useSyncExternalStore(subscribe, () => selector(state));
}

// ---------------------------------------------------------------- navigation

export function navigate(page: Page) {
  set({ page, selectedId: null });
}

export function selectApp(id: string | null) {
  set({ selectedId: id });
}

// -------------------------------------------------------------------- toasts

let toastSeq = 0;

export function toast(tone: Toast["tone"], title: string, detail?: string) {
  const id = ++toastSeq;
  set({ toasts: [...state.toasts.slice(-3), { id, tone, title, detail }] });
  window.setTimeout(() => dismissToast(id), tone === "error" ? 9000 : 4500);
}

export function dismissToast(id: number) {
  if (state.toasts.some((t) => t.id === id)) {
    set({ toasts: state.toasts.filter((t) => t.id !== id) });
  }
}

// --------------------------------------------------------------------- theme

const systemLight = window.matchMedia("(prefers-color-scheme: light)");

function applyTheme(theme: Theme) {
  const resolved = theme === "system" ? (systemLight.matches ? "light" : "dark") : theme;
  document.documentElement.setAttribute("data-theme", resolved);
  try {
    localStorage.setItem("archaea.theme", theme);
  } catch {
    /* storage unavailable: the theme still applies for this session */
  }
}

systemLight.addEventListener("change", () => {
  if (state.settings?.theme === "system") applyTheme("system");
});

// ------------------------------------------------------------------- entries

function pickPackage(app: CatalogApp, arch: Arch): { pkg: Package; arch: Arch } | null {
  const native = app.packages[arch];
  if (native) return { pkg: native, arch };
  // Windows on ARM runs x64 apps under emulation.
  if (arch === "arm64" && app.packages.x64) return { pkg: app.packages.x64, arch: "x64" };
  return null;
}

export function buildEntries(
  catalog: Catalog | null,
  installed: InstalledApp[],
  arch: Arch
): AppEntry[] {
  const byId = new Map(installed.map((a) => [a.id, a]));
  const entries: AppEntry[] = [];

  for (const app of catalog?.apps ?? []) {
    const inst = byId.get(app.id) ?? null;
    byId.delete(app.id);
    const picked = app.status === "available" ? pickPackage(app, arch) : null;

    let entryState: EntryState;
    if (inst) {
      const newer =
        picked && app.version ? compareVersions(app.version, inst.version) > 0 : false;
      entryState = newer ? "update-available" : "installed";
    } else if (app.status === "coming-soon") {
      entryState = "coming-soon";
    } else if (!picked) {
      entryState = "unsupported";
    } else {
      entryState = "not-installed";
    }

    entries.push({
      id: app.id,
      name: app.name,
      tagline: app.tagline,
      description: app.description,
      icon: app.icon ?? inst?.icon ?? null,
      category: app.category,
      featured: app.featured,
      state: entryState,
      catalog: app,
      installed: inst,
      pkg: picked?.pkg ?? null,
      pkgArch: picked?.arch ?? null,
    });
  }

  // Installed apps that are no longer (or not yet) in the catalog — for
  // example when offline with no cache. They stay launchable and removable.
  for (const inst of byId.values()) {
    entries.push({
      id: inst.id,
      name: inst.name,
      tagline: inst.tagline,
      description: "",
      icon: inst.icon ?? null,
      category: "",
      featured: false,
      state: "installed",
      catalog: null,
      installed: inst,
      pkg: null,
      pkgArch: null,
    });
  }
  return entries;
}

export function useEntries(): AppEntry[] {
  const catalog = useStore((s) => s.catalog);
  const installed = useStore((s) => s.installed);
  const arch = useStore((s) => s.system?.arch ?? "x64");
  return useMemo(() => buildEntries(catalog, installed, arch), [catalog, installed, arch]);
}

function currentEntries(): AppEntry[] {
  return buildEntries(state.catalog, state.installed, state.system?.arch ?? "x64");
}

// ------------------------------------------------------------------- catalog

async function reloadInstalled() {
  set({ installed: await api.installed() });
}

export async function refreshCatalog(opts: { silent?: boolean } = {}) {
  if (state.refreshing) return;
  set({ refreshing: true });
  try {
    const result = await api.loadCatalog(true);
    set({
      catalog: result.catalog ?? state.catalog,
      catalogSource: result.catalog ? result.source : state.catalogSource,
      catalogFetchedAt: result.fetchedAt ?? state.catalogFetchedAt,
      catalogError: result.error,
    });
    if (result.error && !opts.silent) {
      toast(
        "error",
        "Couldn't reach the app catalog",
        result.catalog ? "Showing the last saved copy. " + result.error : result.error
      );
    } else if (!result.error) {
      if (result.warnings.length) console.warn("Catalog warnings:", result.warnings);
      if (!opts.silent) {
        const n = currentEntries().filter((e) => e.state === "update-available").length;
        toast("success", n ? `${n} update${n > 1 ? "s" : ""} available` : "Everything is up to date");
      }
      void notifyUpdates();
    }
  } catch (e) {
    set({ catalogError: toError(e).message });
  } finally {
    set({ refreshing: false });
  }
}

/** One system notification per new set of available updates. */
async function notifyUpdates() {
  if (!state.settings?.notifyUpdates) return;
  const updates = currentEntries().filter((e) => e.state === "update-available");
  if (!updates.length) return;
  const signature = updates.map((e) => `${e.id}@${e.catalog?.version}`).join(",");
  try {
    if (localStorage.getItem("archaea.notified") === signature) return;
    let granted = await isPermissionGranted();
    if (!granted) granted = (await requestPermission()) === "granted";
    if (!granted) return;
    sendNotification({
      title: updates.length === 1 ? `${updates[0].name} update available` : "ArchaeaSuite updates available",
      body:
        updates.length === 1
          ? `Version ${updates[0].catalog?.version} is ready to install.`
          : `${updates.map((e) => e.name).join(", ")} can be updated.`,
    });
    localStorage.setItem("archaea.notified", signature);
  } catch {
    /* notifications are best-effort */
  }
}

// ---------------------------------------------------------------- operations

function setOp(id: string, op: Op | null) {
  const ops = { ...state.ops };
  if (op) ops[id] = op;
  else delete ops[id];
  set({ ops });
}

const DONE_VERB: Record<OpKind, string> = {
  install: "installed",
  update: "updated",
  repair: "repaired",
  uninstall: "uninstalled",
};

export async function installApp(id: string, kind: Exclude<OpKind, "uninstall"> = "install") {
  if (state.ops[id]) return;
  const name = currentEntries().find((e) => e.id === id)?.name ?? id;
  setOp(id, { kind, phase: "starting", done: 0, total: 0 });
  try {
    const app = await api.install(id);
    await reloadInstalled();
    toast("success", `${name} ${DONE_VERB[kind]}`, `Version ${app.version}`);
  } catch (e) {
    const err = toError(e);
    if (err.code === "cancelled") {
      toast("info", `${name}: cancelled`, "Nothing was changed.");
    } else {
      const rolledBack = kind === "install" ? "" : " Your existing version was kept.";
      toast("error", `${name} could not be ${DONE_VERB[kind]}`, err.message + rolledBack);
    }
    await reloadInstalled().catch(() => {});
  } finally {
    setOp(id, null);
  }
}

export function cancelOp(id: string) {
  void api.cancelInstall(id);
}

export async function updateAll() {
  const pending = currentEntries().filter((e) => e.state === "update-available" && !state.ops[e.id]);
  // Sequential: predictable disk and network use on modest machines.
  for (const entry of pending) await installApp(entry.id, "update");
}

export function requestUninstall(id: string | null) {
  set({ confirmUninstallId: id });
}

export async function uninstallApp(id: string) {
  set({ confirmUninstallId: null });
  if (state.ops[id]) return;
  const name = state.installed.find((a) => a.id === id)?.name ?? id;
  setOp(id, { kind: "uninstall", phase: "removing", done: 0, total: 0 });
  try {
    await api.uninstall(id);
    if (state.selectedId === id) set({ selectedId: null });
    toast("success", `${name} uninstalled`);
  } catch (e) {
    toast("error", `${name} could not be uninstalled`, toError(e).message);
  } finally {
    await reloadInstalled().catch(() => {});
    setOp(id, null);
  }
}

export async function launchApp(id: string) {
  const name = state.installed.find((a) => a.id === id)?.name ?? id;
  try {
    await api.launch(id);
    toast("info", `Launching ${name}`);
  } catch (e) {
    toast("error", `${name} could not be launched`, toError(e).message);
    await reloadInstalled().catch(() => {});
  }
}

export async function openAppFolder(id: string) {
  try {
    await api.openAppFolder(id);
  } catch (e) {
    toast("error", "Couldn't open the folder", toError(e).message);
  }
}

// ------------------------------------------------------------------ settings

export async function saveSettings(patch: Partial<Settings>): Promise<boolean> {
  if (!state.settings) return false;
  try {
    const saved = await api.setSettings({ ...state.settings, ...patch });
    set({ settings: saved });
    applyTheme(saved.theme);
    return true;
  } catch (e) {
    toast("error", "Couldn't save settings", toError(e).message);
    return false;
  }
}

// ------------------------------------------------- launcher self-update
// Entirely separate from app updates: Tauri's updater verifies a signature
// made with the launcher's own release key before installing anything.

let pendingUpdate: Update | null = null;

export async function checkLauncherUpdate(opts: { silent?: boolean } = {}) {
  if (state.launcher.status === "checking" || state.launcher.status === "installing") return;
  set({ launcher: { status: "checking" } });
  try {
    const update = await check();
    if (update) {
      pendingUpdate = update;
      set({ launcher: { status: "available", version: update.version, notes: update.body ?? "" } });
    } else {
      set({ launcher: { status: "up-to-date" } });
    }
  } catch (e) {
    // Silent on startup: being offline or having no published release yet is normal.
    set({
      launcher: opts.silent ? { status: "idle" } : { status: "error", error: toError(e).message },
    });
  }
}

export async function installLauncherUpdate() {
  const update = pendingUpdate;
  if (!update) return;
  if (Object.keys(state.ops).length) {
    toast("info", "Finish running installs first", "The launcher restarts to update itself.");
    return;
  }
  const version = update.version;
  let done = 0;
  let total = 0;
  set({ launcher: { status: "installing", version, done, total } });
  try {
    await update.downloadAndInstall((event) => {
      if (event.event === "Started") total = event.data.contentLength ?? 0;
      else if (event.event === "Progress") done += event.data.chunkLength;
      set({ launcher: { status: "installing", version, done, total } });
    });
    await relaunch();
  } catch (e) {
    set({ launcher: { status: "error", version, error: toError(e).message } });
  }
}

// ---------------------------------------------------------------------- init

const SIX_HOURS = 6 * 60 * 60 * 1000;
let started = false;

export async function init() {
  if (started) return;
  started = true;

  await listen<Progress>("install-progress", ({ payload }) => {
    const op = state.ops[payload.id];
    if (!op) return;
    setOp(payload.id, { ...op, phase: payload.phase, done: payload.done, total: payload.total });
  });

  try {
    const [system, settings, installed, cached] = await Promise.all([
      api.systemInfo(),
      api.settings(),
      api.installed(),
      api.loadCatalog(false),
    ]);
    applyTheme(settings.theme);
    set({
      system,
      settings,
      installed,
      catalog: cached.catalog,
      catalogSource: cached.source,
      catalogFetchedAt: cached.fetchedAt,
    });
  } catch (e) {
    toast("error", "ArchaeaSuite could not start properly", toError(e).message);
  }
  set({ ready: true });

  // The UI is usable from the cache straight away; the network comes after.
  // With automatic checks off, only fetch when there is nothing to show.
  const auto = state.settings?.autoCheckUpdates ?? true;
  if (auto || !state.catalog) void refreshCatalog({ silent: true });
  if (auto) void checkLauncherUpdate({ silent: true });

  window.setInterval(() => {
    if (state.settings?.autoCheckUpdates) {
      void refreshCatalog({ silent: true });
      void checkLauncherUpdate({ silent: true });
    }
  }, SIX_HOURS);
}
