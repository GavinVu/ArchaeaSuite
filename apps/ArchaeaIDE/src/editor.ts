// Monaco setup, open documents, and the tab/group actions built on them.
import * as monaco from "monaco-editor";
import EditorWorker from "monaco-editor/editor/editor.worker?worker";
import JsonWorker from "monaco-editor/language/json/json.worker?worker";
import CssWorker from "monaco-editor/language/css/css.worker?worker";
import HtmlWorker from "monaco-editor/language/html/html.worker?worker";
import TsWorker from "monaco-editor/language/typescript/ts.worker?worker";
import { api, basename, errorText, samePath } from "./api";
import { ask } from "./overlay";
import { fail, get, onChange, saveSession, set, toast, type Group, type Tab, type TabKind } from "./store";

export { monaco };

self.MonacoEnvironment = {
  getWorker(_id, label) {
    if (label === "json") return new JsonWorker();
    if (label === "css" || label === "scss" || label === "less") return new CssWorker();
    if (label === "html" || label === "handlebars" || label === "razor") return new HtmlWorker();
    if (label === "typescript" || label === "javascript") return new TsWorker();
    return new EditorWorker();
  },
};

// Colours mirror the tokens in launcher/src/styles.css.
const theme = (dark: boolean): monaco.editor.IStandaloneThemeData => {
  const c = dark
    ? { bg: "#161616", surface: "#242424", line: "#3d3d3a", text: "#f5f5f0", dim: "#b4b4ab", faint: "#85857e", accent: "#d6ff00" }
    : { bg: "#ffffff", surface: "#f5f5f0", line: "#161616", text: "#161616", dim: "#4d4d47", faint: "#72726a", accent: "#161616" };
  const t = dark
    ? { kw: "d6ff00", str: "ffb86b", num: "7fd4ff", com: "85857e", type: "9be8c8", fn: "f5f5f0", tag: "d6ff00", attr: "9be8c8" }
    : { kw: "5b21b6", str: "9a3412", num: "075985", com: "72726a", type: "0f766e", fn: "161616", tag: "5b21b6", attr: "0f766e" };
  return {
    base: dark ? "vs-dark" : "vs",
    inherit: true,
    rules: [
      { token: "comment", foreground: t.com, fontStyle: "italic" },
      { token: "keyword", foreground: t.kw },
      { token: "string", foreground: t.str },
      { token: "number", foreground: t.num },
      { token: "regexp", foreground: t.str },
      { token: "type", foreground: t.type },
      { token: "tag", foreground: t.tag },
      { token: "attribute.name", foreground: t.attr },
      { token: "attribute.value", foreground: t.str },
      { token: "delimiter", foreground: dark ? "b4b4ab" : "4d4d47" },
    ],
    colors: {
      "editor.background": c.bg,
      "editor.foreground": c.text,
      "editorLineNumber.foreground": c.faint,
      "editorLineNumber.activeForeground": c.text,
      "editor.lineHighlightBackground": dark ? "#1f1f1f" : "#f5f5f0",
      "editor.lineHighlightBorder": "#00000000",
      "editor.selectionBackground": dark ? "#d6ff0040" : "#d6ff00",
      "editor.inactiveSelectionBackground": dark ? "#d6ff0022" : "#d6ff0080",
      "editor.findMatchBackground": dark ? "#d6ff0066" : "#d6ff00",
      "editor.findMatchHighlightBackground": dark ? "#d6ff0030" : "#d6ff0070",
      "editorCursor.foreground": dark ? "#d6ff00" : "#161616",
      "editorIndentGuide.background1": dark ? "#2e2e2e" : "#e9e9e1",
      "editorIndentGuide.activeBackground1": c.faint,
      "editorWidget.background": c.surface,
      "editorWidget.border": c.line,
      "editorSuggestWidget.background": c.surface,
      "editorSuggestWidget.border": c.line,
      "editorSuggestWidget.selectedBackground": dark ? "#3d3d3a" : "#d6ff00",
      "editorSuggestWidget.highlightForeground": c.accent,
      "editorHoverWidget.background": c.surface,
      "editorHoverWidget.border": c.line,
      "editorGutter.background": c.bg,
      "minimap.background": c.bg,
      "scrollbarSlider.background": dark ? "#3d3d3a99" : "#16161633",
      "scrollbarSlider.hoverBackground": dark ? "#85857e" : "#16161666",
      "input.background": c.bg,
      "input.border": c.line,
      "focusBorder": dark ? "#d6ff00" : "#161616",
      "list.hoverBackground": dark ? "#2e2e2e" : "#e9e9e1",
      "diffEditor.insertedTextBackground": dark ? "#d6ff0026" : "#d6ff0066",
      "diffEditor.removedTextBackground": dark ? "#ff6b5e30" : "#c2261a26",
      "peekViewEditor.background": c.surface,
      "peekViewResult.background": c.surface,
      "peekView.border": dark ? "#d6ff00" : "#161616",
    },
  };
};
monaco.editor.defineTheme("archaea-dark", theme(true));
monaco.editor.defineTheme("archaea-light", theme(false));

export function editorOptions(): monaco.editor.IEditorOptions & monaco.editor.IGlobalEditorOptions {
  const s = get().settings;
  return {
    theme: s.theme === "dark" ? "archaea-dark" : "archaea-light",
    fontFamily: '"IBM Plex Mono", Consolas, "Cascadia Mono", monospace',
    fontSize: s.fontSize,
    tabSize: s.tabSize,
    wordWrap: s.wordWrap ? "on" : "off",
    minimap: { enabled: s.minimap },
  };
}

// ------------------------------------------------------------- documents

interface Doc {
  path: string;
  model: monaco.editor.ITextModel;
  savedVersion: number;
  mtime: number;
}
const docs = new Map<string, Doc>();
const key = (path: string) => path.toLowerCase();
export const docFor = (path: string) => docs.get(key(path));
export const openDocs = () => [...docs.values()];
export const pathOf = (model: monaco.editor.ITextModel | null) =>
  model && model.uri.scheme === "file" ? openDocs().find((d) => d.model === model)?.path ?? null : null;

const saveListeners = new Set<(path: string) => void>();
export const onDidSave = (fn: (path: string) => void) => void saveListeners.add(fn);
const openListeners = new Set<(doc: Doc) => void>();
export const onDidOpen = (fn: (doc: Doc) => void) => void openListeners.add(fn);

function refreshDirty(doc: Doc) {
  const dirty = doc.model.getAlternativeVersionId() !== doc.savedVersion;
  if (dirty === !!get().dirty[key(doc.path)]) return;
  set((s) => {
    const next = { ...s.dirty };
    if (dirty) next[key(doc.path)] = true;
    else delete next[key(doc.path)];
    return { dirty: next };
  });
}
export const isDirty = (path: string) => !!get().dirty[key(path)];

const autoSave = new Map<string, number>();
async function loadDoc(path: string): Promise<Doc> {
  const existing = docFor(path);
  if (existing) return existing;
  const file = await api.readText(path);
  const model = monaco.editor.createModel(file.text, undefined, monaco.Uri.file(path));
  model.setEOL(file.eol === "\r\n" ? monaco.editor.EndOfLineSequence.CRLF : monaco.editor.EndOfLineSequence.LF);
  const doc: Doc = { path, model, savedVersion: model.getAlternativeVersionId(), mtime: file.mtime };
  model.onDidChangeContent(() => {
    refreshDirty(doc);
    if (!get().settings.autoSave) return;
    clearTimeout(autoSave.get(key(path)));
    autoSave.set(key(path), window.setTimeout(() => isDirty(path) && void saveFile(path, true), 1000));
  });
  docs.set(key(path), doc);
  openListeners.forEach((fn) => fn(doc));
  return doc;
}

export async function saveFile(path: string, quiet = false): Promise<boolean> {
  const doc = docFor(path);
  if (!doc) return false;
  try {
    const editor = activeEditor();
    if (!quiet && get().settings.formatOnSave && editor?.getModel() === doc.model) {
      await editor.getAction("editor.action.formatDocument")?.run();
    }
    doc.mtime = await api.writeText(path, doc.model.getValue());
    doc.savedVersion = doc.model.getAlternativeVersionId();
    refreshDirty(doc);
    saveListeners.forEach((fn) => fn(path));
    return true;
  } catch (e) {
    toast("error", `Could not save ${basename(path)}`, errorText(e));
    return false;
  }
}

export async function saveAll() {
  for (const doc of openDocs()) if (isDirty(doc.path)) await saveFile(doc.path, true);
}

/** Reloads unmodified documents whose file changed on disk (after git, replace, external edits). */
export async function syncWithDisk() {
  for (const doc of openDocs()) {
    const stat = await api.stat(doc.path);
    if (!stat.exists || stat.mtime === doc.mtime || isDirty(doc.path)) continue;
    try {
      const file = await api.readText(doc.path);
      doc.mtime = file.mtime;
      if (file.text !== doc.model.getValue()) doc.model.setValue(file.text);
      doc.savedVersion = doc.model.getAlternativeVersionId();
      refreshDirty(doc);
    } catch {
      /* unreadable now; keep what is open */
    }
  }
}

// ------------------------------------------------------- editors and tabs

const editors = new Map<number, monaco.editor.IStandaloneCodeEditor>();
const viewStates = new Map<string, monaco.editor.ICodeEditorViewState | null>();
/** Original (left-hand) text for each open diff tab. */
export const diffOriginals = new Map<string, monaco.editor.ITextModel>();

export function attachEditor(groupId: number, editor: monaco.editor.IStandaloneCodeEditor | null) {
  if (editor) editors.set(groupId, editor);
  else editors.delete(groupId);
}
export const editorOf = (groupId: number) => editors.get(groupId) ?? null;
export const activeEditor = () => editors.get(get().activeGroup) ?? null;
export const allEditors = () => [...editors.values()];

const group = (id = get().activeGroup): Group => get().groups.find((g) => g.id === id) ?? get().groups[0];
const updateGroup = (id: number, patch: Partial<Group>) => {
  set((s) => ({ groups: s.groups.map((g) => (g.id === id ? { ...g, ...patch } : g)) }));
  saveSession();
};
export const activeTab = (): Tab | null => group().tabs.find((t) => t.id === group().active) ?? null;
export const activeFile = (): string | null => {
  const tab = activeTab();
  return tab?.kind === "file" ? tab.path! : null;
};

export function rememberView(groupId: number, tabId: string) {
  const editor = editors.get(groupId);
  if (editor?.getModel()) viewStates.set(`${groupId}:${tabId}`, editor.saveViewState());
}

/** Points a group's editor at a tab's document. Called by the group component. */
export function showInEditor(groupId: number, tab: Tab) {
  const editor = editors.get(groupId);
  const doc = tab.path ? docFor(tab.path) : undefined;
  if (!editor || !doc) return;
  if (editor.getModel() !== doc.model) {
    editor.setModel(doc.model);
    const state = viewStates.get(`${groupId}:${tab.id}`);
    if (state) editor.restoreViewState(state);
  }
}

function addTab(tab: Tab, groupId = get().activeGroup) {
  const g = group(groupId);
  const existing = g.tabs.find((t) => t.id === tab.id);
  if (g.active) rememberView(g.id, g.active);
  updateGroup(g.id, { tabs: existing ? g.tabs : [...g.tabs, tab], active: tab.id });
  set({ activeGroup: g.id });
}

export interface Reveal { line: number; col?: number; len?: number }

export async function openFile(path: string, reveal?: Reveal, groupId?: number): Promise<boolean> {
  try {
    const doc = await loadDoc(path);
    const gid = groupId ?? get().activeGroup;
    addTab({ id: key(doc.path), kind: "file", path: doc.path, title: basename(doc.path) }, gid);
    // The group component swaps the model in an effect; wait for it.
    await new Promise((r) => setTimeout(r, 0));
    showInEditor(gid, { id: key(doc.path), kind: "file", path: doc.path, title: "" });
    const editor = editors.get(gid);
    if (editor && reveal) {
      const col = reveal.col ?? 1;
      editor.setSelection(new monaco.Range(reveal.line, col, reveal.line, col + (reveal.len ?? 0)));
      editor.revealLineInCenterIfOutsideViewport(reveal.line);
    }
    editor?.focus();
    return true;
  } catch (e) {
    toast("error", `Could not open ${basename(path)}`, errorText(e));
    return false;
  }
}

export function openSpecial(kind: Exclude<TabKind, "file" | "diff">) {
  const title = { settings: "Settings", keys: "Keyboard Shortcuts", welcome: "Welcome" }[kind];
  addTab({ id: `archaea:${kind}`, kind, title });
}

/** Opens a side-by-side comparison of `original` against the live file. */
export async function openDiff(path: string, original: string, label: string) {
  const doc = await loadDoc(path).catch(fail(`Could not open ${basename(path)}`));
  if (!doc) return;
  const id = `diff:${key(path)}`;
  diffOriginals.get(id)?.dispose();
  diffOriginals.set(id, monaco.editor.createModel(original, doc.model.getLanguageId()));
  addTab({ id, kind: "diff", path: doc.path, title: `${basename(path)} (${label})` });
}

function disposeIfUnused(tab: Tab) {
  if (get().groups.some((g) => g.tabs.some((t) => t.id === tab.id))) return;
  if (tab.kind === "diff") {
    diffOriginals.get(tab.id)?.dispose();
    diffOriginals.delete(tab.id);
  }
  const path = tab.path;
  if (!path || get().groups.some((g) => g.tabs.some((t) => t.path && samePath(t.path, path)))) return;
  const doc = docFor(path);
  if (!doc) return;
  doc.model.dispose();
  docs.delete(key(path));
  set((s) => {
    const next = { ...s.dirty };
    delete next[key(path)];
    return { dirty: next };
  });
}

export async function closeTab(groupId: number, tabId: string, force = false): Promise<boolean> {
  const g = group(groupId);
  const tab = g.tabs.find((t) => t.id === tabId);
  if (!tab) return true;
  const elsewhere = get().groups.some((o) => o.id !== g.id && o.tabs.some((t) => t.id === tab.id));
  if (!force && tab.kind === "file" && isDirty(tab.path!) && !elsewhere) {
    const answer = await ask(`Save changes to ${tab.title}?`, "Your changes will be lost if you don't save them.", [
      { label: "Save", value: "save", kind: "primary" },
      { label: "Don't save", value: "discard", kind: "danger" },
      { label: "Cancel", value: "cancel" },
    ]);
    if (answer === null || answer === "cancel") return false;
    if (answer === "save" && !(await saveFile(tab.path!))) return false;
  }
  const index = g.tabs.indexOf(tab);
  const tabs = g.tabs.filter((t) => t !== tab);
  const active = g.active === tab.id ? (tabs[Math.min(index, tabs.length - 1)]?.id ?? null) : g.active;
  viewStates.delete(`${g.id}:${tab.id}`);
  if (tabs.length === 0 && get().groups.length > 1) {
    set((s) => {
      const groups = s.groups.filter((o) => o.id !== g.id);
      return { groups, activeGroup: groups[0].id };
    });
    saveSession();
  } else {
    if (tabs.length === 0) editors.get(g.id)?.setModel(null);
    updateGroup(g.id, { tabs, active });
  }
  disposeIfUnused(tab);
  return true;
}

export async function closeAllTabs(): Promise<boolean> {
  for (const g of [...get().groups]) for (const t of [...g.tabs]) if (!(await closeTab(g.id, t.id))) return false;
  return true;
}

export function selectTab(groupId: number, tabId: string) {
  const g = group(groupId);
  if (g.active && g.active !== tabId) rememberView(g.id, g.active);
  updateGroup(groupId, { active: tabId });
  set({ activeGroup: groupId });
}

/** Opens the active tab in a new group to the right. */
export function splitEditor() {
  const tab = activeTab();
  if (!tab || get().groups.length >= 3) return;
  const id = Math.max(...get().groups.map((g) => g.id)) + 1;
  set((s) => ({ groups: [...s.groups, { id, tabs: [tab], active: tab.id }], activeGroup: id }));
  saveSession();
}

export function cycleTab(step: number) {
  const g = group();
  if (g.tabs.length < 2) return;
  const i = g.tabs.findIndex((t) => t.id === g.active);
  selectTab(g.id, g.tabs[(i + step + g.tabs.length) % g.tabs.length].id);
}

/** Keeps tabs and documents pointing at a file or folder that was renamed. */
export function pathRenamed(from: string, to: string) {
  const moved = (p: string) =>
    samePath(p, from) ? to : p.toLowerCase().startsWith(from.toLowerCase() + "\\") ? to + p.slice(from.length) : null;
  for (const doc of openDocs()) {
    const next = moved(doc.path);
    if (!next) continue;
    // A model's URI is fixed, so the document is re-created under its new path.
    const text = doc.model.getValue();
    const dirty = isDirty(doc.path);
    const oldId = key(doc.path);
    const model = monaco.editor.createModel(text, undefined, monaco.Uri.file(next));
    allEditors().forEach((e) => e.getModel() === doc.model && e.setModel(model));
    doc.model.dispose();
    docs.delete(oldId);
    const fresh: Doc = { path: next, model, savedVersion: dirty ? -1 : model.getAlternativeVersionId(), mtime: doc.mtime };
    model.onDidChangeContent(() => refreshDirty(fresh));
    docs.set(key(next), fresh);
    set((s) => {
      const d = { ...s.dirty };
      delete d[oldId];
      if (dirty) d[key(next)] = true;
      return {
        dirty: d,
        groups: s.groups.map((g) => ({
          ...g,
          active: g.active === oldId ? key(next) : g.active,
          tabs: g.tabs.map((t) => (t.id === oldId ? { ...t, id: key(next), path: next, title: basename(next) } : t)),
        })),
      };
    });
    openListeners.forEach((fn) => fn(fresh));
  }
  saveSession();
}

export const runEditorAction = (id: string) => {
  const editor = activeEditor();
  editor?.focus();
  void editor?.getAction(id)?.run();
};

// Settings changes reach every open editor.
let lastSettings = get().settings;
onChange(() => {
  const s = get().settings;
  if (s === lastSettings) return;
  lastSettings = s;
  const options = editorOptions();
  monaco.editor.setTheme(options.theme!);
  allEditors().forEach((e) => e.updateOptions(options));
  openDocs().forEach((d) => d.model.updateOptions({ tabSize: s.tabSize }));
});
