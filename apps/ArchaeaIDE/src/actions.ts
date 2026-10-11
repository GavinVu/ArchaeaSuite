// Every built-in command, and the menus that list them.
import { revealItemInDir } from "@tauri-apps/plugin-opener";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { api, basename, join, relative } from "./api";
import { allCommands, keyFor, register, type Command } from "./commands";
import { debug, debugState, startDebugging, toggleBreakpoint, clearBreakpoints } from "./debug";
import {
  activeEditor, activeFile, activeTab, closeTab, cycleTab, openFile, openSpecial, runEditorAction, saveAll, saveFile,
  splitEditor,
} from "./editor";
import { createSampleExtension, extensionsDir, loadExtensions } from "./extensions";
import { branches, git, gitState, refreshGit } from "./git";
import { servers, startServer, stopServer } from "./lsp";
import { pick, prompt } from "./overlay";
import { fail, get, reloadConfig, saveSettings, set, toast, touchFs, type PanelView, type SideView } from "./store";
import { configureTasks, focusTerminal, newTerminal, pickTask, runActiveFile, sendToTerminal, stopRun } from "./terminal";
import { closeFolder, newProject, newUntitledFile, openFolder, pickFile, pickFolder } from "./workspace";

const showSide = (side: SideView) => () => {
  const s = get();
  set(s.sideOpen && s.side === side ? { sideOpen: false } : { side, sideOpen: true });
};
const showPanel = (panel: PanelView) => () => set({ panel, panelOpen: true });
const editorAction = (id: string) => () => runEditorAction(id);

export async function commandPalette() {
  const list = allCommands().sort((a, b) => (a.category + a.title).localeCompare(b.category + b.title));
  const chosen = await pick(
    list.map((c) => ({ label: `${c.category}: ${c.title}`, hint: keyFor(c), value: c })),
    "Type a command"
  );
  await chosen?.run();
}

export async function quickOpen() {
  const root = get().root;
  if (!root) return toast("info", "Open a folder to search its files.");
  const files = await api.listFiles(root);
  const chosen = await pick(
    files.map((f) => ({ label: f.split("/").pop()!, detail: f, value: f })),
    "Go to file"
  );
  if (chosen) await openFile(join(root, chosen.replace(/\//g, "\\")));
}

export async function switchBranch() {
  if (!gitState().repo) return toast("info", "This folder is not a Git repository.");
  const names = await branches().catch(() => []);
  const chosen = await pick(
    [{ label: "Create new branch…", value: "+" }, ...names.map((n) => ({ label: n, hint: n === gitState().branch ? "current" : "", value: n }))],
    "Switch to branch"
  );
  if (!chosen) return;
  try {
    if (chosen === "+") {
      const name = (await prompt("New branch name"))?.trim();
      if (!name) return;
      await git(["switch", "-c", name]);
    } else {
      await git(["switch", chosen]);
    }
    touchFs();
  } catch (e) {
    fail("Could not switch branch")(e);
  }
  const { syncWithDisk } = await import("./editor");
  await syncWithDisk();
  await refreshGit();
}

async function chooseLanguageServer() {
  const language = activeEditor()?.getModel()?.getLanguageId();
  if (!language || !(servers[language] || get().settings.serverCommands[language]))
    return toast("info", "No language server is known for this file type.", "Built-in support covers JavaScript, TypeScript, JSON, CSS and HTML.");
  stopServer(language);
  await new Promise((r) => setTimeout(r, 300));
  await startServer(language, true);
}

const openConfigFile = async (name: string, fallback: unknown) => {
  const path = join(get().info!.dataDir, name);
  if (!(await api.stat(path)).exists) await api.writeText(path, JSON.stringify(fallback, null, 2) + "\n");
  await openFile(path);
};

export function registerCommands() {
  const win = getCurrentWindow();
  const c = (category: string, id: string, title: string, run: () => unknown, key?: string): Command => ({ id, title, category, key, run });
  register([
    c("File", "file.newProject", "New Project…", newProject, "Ctrl+Shift+N"),
    c("File", "file.new", "New File…", newUntitledFile, "Ctrl+N"),
    c("File", "file.open", "Open File…", pickFile, "Ctrl+O"),
    c("File", "file.openFolder", "Open Folder…", pickFolder, "Ctrl+K"),
    c("File", "file.openRecent", "Open Recent…", async () => {
      const path = await pick(get().recent.map((r) => ({ label: basename(r), detail: r, value: r })), "Open a recent folder");
      if (path) await openFolder(path);
    }, "Ctrl+R"),
    c("File", "file.closeFolder", "Close Folder", closeFolder),
    c("File", "file.save", "Save", () => { const f = activeFile(); return f && saveFile(f); }, "Ctrl+S"),
    c("File", "file.saveAll", "Save All", saveAll, "Ctrl+Alt+S"),
    c("File", "file.close", "Close Editor", () => { const t = activeTab(); return t && closeTab(get().activeGroup, t.id); }, "Ctrl+W"),
    c("File", "file.reveal", "Reveal Active File in File Explorer", () => { const f = activeFile(); return f && revealItemInDir(f); }),
    c("File", "file.copyPath", "Copy Path of Active File", () => { const f = activeFile(); return f && navigator.clipboard.writeText(f); }),
    c("File", "file.copyRelativePath", "Copy Relative Path of Active File", () => {
      const f = activeFile();
      return f && navigator.clipboard.writeText(get().root ? relative(get().root!, f) : f);
    }),
    c("File", "file.settings", "Settings", () => openSpecial("settings"), "Ctrl+,"),
    c("File", "file.settingsJson", "Open settings.json", () => openConfigFile("settings.json", get().settings)),
    c("File", "file.reloadSettings", "Reload Settings from Disk", reloadConfig),
    c("File", "file.shortcuts", "Keyboard Shortcuts", () => openSpecial("keys")),
    c("File", "file.exit", "Exit", () => win.close()),

    c("Edit", "edit.undo", "Undo", () => activeEditor()?.trigger("menu", "undo", null)),
    c("Edit", "edit.redo", "Redo", () => activeEditor()?.trigger("menu", "redo", null)),
    c("Edit", "edit.find", "Find", editorAction("actions.find")),
    c("Edit", "edit.replace", "Replace", editorAction("editor.action.startFindReplaceAction")),
    c("Edit", "edit.findInFiles", "Find in Files", () => set({ side: "search", sideOpen: true }), "Ctrl+Shift+F"),
    c("Edit", "edit.format", "Format Document", editorAction("editor.action.formatDocument"), "Shift+Alt+F"),
    c("Edit", "edit.comment", "Toggle Line Comment", editorAction("editor.action.commentLine")),
    c("Edit", "edit.cursorAbove", "Add Cursor Above", editorAction("editor.action.insertCursorAbove")),
    c("Edit", "edit.cursorBelow", "Add Cursor Below", editorAction("editor.action.insertCursorBelow")),
    c("Edit", "edit.selectNext", "Add Next Occurrence to Selection", editorAction("editor.action.addSelectionToNextFindMatch")),
    c("Edit", "edit.selectAllOccurrences", "Select All Occurrences", editorAction("editor.action.selectHighlights")),

    c("Go", "go.file", "Go to File…", quickOpen, "Ctrl+P"),
    c("Go", "go.line", "Go to Line…", editorAction("editor.action.gotoLine"), "Ctrl+G"),
    c("Go", "go.symbol", "Go to Symbol in File…", editorAction("editor.action.quickOutline"), "Ctrl+Shift+O"),
    c("Go", "go.definition", "Go to Definition", editorAction("editor.action.revealDefinition")),
    c("Go", "go.references", "Find All References", editorAction("editor.action.goToReferences")),
    c("Go", "go.rename", "Rename Symbol", editorAction("editor.action.rename")),
    c("Go", "go.nextProblem", "Next Problem", editorAction("editor.action.marker.nextInFiles")),
    c("Go", "go.nextTab", "Next Editor Tab", () => cycleTab(1), "Ctrl+Tab"),
    c("Go", "go.prevTab", "Previous Editor Tab", () => cycleTab(-1), "Ctrl+Shift+Tab"),

    c("View", "view.palette", "Command Palette…", commandPalette, "Ctrl+Shift+P"),
    c("View", "view.explorer", "Explorer", showSide("explorer"), "Ctrl+Shift+E"),
    c("View", "view.search", "Search", showSide("search")),
    c("View", "view.git", "Source Control", showSide("git"), "Ctrl+Shift+G"),
    c("View", "view.debug", "Run and Debug", showSide("debug"), "Ctrl+Shift+D"),
    c("View", "view.extensions", "Extensions", showSide("extensions"), "Ctrl+Shift+X"),
    c("View", "view.ai", "Local AI Assistant", showSide("ai"), "Ctrl+Shift+A"),
    c("View", "view.sidebar", "Toggle Side Bar", () => set((s) => ({ sideOpen: !s.sideOpen })), "Ctrl+B"),
    c("View", "view.panel", "Toggle Panel", () => set((s) => ({ panelOpen: !s.panelOpen })), "Ctrl+J"),
    c("View", "view.problems", "Problems", showPanel("problems"), "Ctrl+Shift+M"),
    c("View", "view.output", "Output", showPanel("output")),
    c("View", "view.split", "Split Editor Right", splitEditor, "Ctrl+\\"),
    c("View", "view.minimap", "Toggle Minimap", () => saveSettings({ minimap: !get().settings.minimap })),
    c("View", "view.wordWrap", "Toggle Word Wrap", () => saveSettings({ wordWrap: !get().settings.wordWrap }), "Alt+Z"),
    c("View", "view.theme", "Toggle Light/Dark Theme", () => saveSettings({ theme: get().settings.theme === "dark" ? "light" : "dark" })),
    c("View", "view.zoomIn", "Increase Font Size", () => saveSettings({ fontSize: Math.min(32, get().settings.fontSize + 1) }), "Ctrl+="),
    c("View", "view.zoomOut", "Decrease Font Size", () => saveSettings({ fontSize: Math.max(8, get().settings.fontSize - 1) }), "Ctrl+-"),
    c("View", "view.welcome", "Welcome", () => openSpecial("welcome")),

    c("Run", "run.run", "Run Active File", () => runActiveFile(), "F5"),
    c("Run", "run.stop", "Stop", () => (debugState().status !== "idle" ? debug.stop() : stopRun()), "Shift+F5"),
    c("Run", "run.debug", "Start Debugging / Continue", startDebugging, "F6"),
    c("Run", "run.stepOver", "Step Over", debug.next, "F10"),
    c("Run", "run.stepInto", "Step Into", debug.stepIn, "F11"),
    c("Run", "run.stepOut", "Step Out", debug.stepOut, "Shift+F11"),
    c("Run", "run.breakpoint", "Toggle Breakpoint", () => {
      const f = activeFile();
      const line = activeEditor()?.getPosition()?.lineNumber;
      if (f && line) toggleBreakpoint(f, line);
    }, "F9"),
    c("Run", "run.clearBreakpoints", "Remove All Breakpoints", clearBreakpoints),
    c("Run", "run.task", "Run Task…", pickTask, "Ctrl+Shift+B"),
    c("Run", "run.configureTasks", "Configure Tasks", () => configureTasks(openFile)),
    c("Run", "run.selection", "Run Selected Text in Terminal", async () => {
      const editor = activeEditor();
      const selection = editor?.getSelection();
      const text = selection && !selection.isEmpty() ? editor!.getModel()!.getValueInRange(selection) : "";
      if (!text.trim()) return toast("info", "Select the text to send to the terminal first.");
      await sendToTerminal(text.replace(/\r?\n/g, "\r") + "\r");
    }),

    c("Terminal", "terminal.new", "New Terminal", () => newTerminal(), "Ctrl+Shift+`"),
    c("Terminal", "terminal.toggle", "Toggle Terminal", async () => {
      const s = get();
      if (s.panelOpen && s.panel === "terminal") return set({ panelOpen: false });
      set({ panel: "terminal", panelOpen: true });
      if (s.terminals.length === 0) await newTerminal();
      else setTimeout(focusTerminal, 50);
    }, "Ctrl+`"),

    c("Git", "git.branch", "Switch Branch…", switchBranch),
    c("Git", "git.refresh", "Refresh", () => { touchFs(); return refreshGit(); }),
    c("Git", "git.init", "Initialize Repository", async () => {
      if (!get().root) return toast("info", "Open a folder first.");
      await git(["init"]).then(() => toast("ok", "Initialized an empty Git repository")).catch(fail("git init failed"));
      await refreshGit();
    }),

    c("Language", "lsp.restart", "Start or Restart Language Server", chooseLanguageServer),
    c("Extensions", "ext.reload", "Reload Extensions", loadExtensions),
    c("Extensions", "ext.sample", "Create Sample Extension", async () => {
      await openFile(join(await createSampleExtension(), "main.js"));
      set({ side: "extensions", sideOpen: true });
    }),
    c("Extensions", "ext.folder", "Open Extensions Folder", async () => {
      await api.createDir(extensionsDir()).catch(() => {});
      await revealItemInDir(extensionsDir());
    }),
  ]);
}

/** Menu bar contents: command ids, with "-" for a separator. */
export const menus: [string, string[]][] = [
  ["File", ["file.newProject", "file.new", "-", "file.open", "file.openFolder", "file.openRecent", "file.closeFolder", "-",
    "file.save", "file.saveAll", "file.close", "-", "file.settings", "file.shortcuts", "-", "file.exit"]],
  ["Edit", ["edit.undo", "edit.redo", "-", "edit.find", "edit.replace", "edit.findInFiles", "-", "edit.format", "edit.comment",
    "-", "edit.cursorAbove", "edit.cursorBelow", "edit.selectNext", "edit.selectAllOccurrences"]],
  ["View", ["view.palette", "-", "view.explorer", "view.search", "view.git", "view.debug", "view.extensions", "view.ai", "-",
    "view.sidebar", "view.panel", "view.problems", "view.output", "view.split", "-", "view.minimap", "view.wordWrap", "view.theme",
    "view.zoomIn", "view.zoomOut"]],
  ["Go", ["go.file", "go.line", "go.symbol", "-", "go.definition", "go.references", "go.rename", "go.nextProblem"]],
  ["Run", ["run.run", "run.stop", "-", "run.debug", "run.stepOver", "run.stepInto", "run.stepOut", "run.breakpoint",
    "run.clearBreakpoints", "-", "run.task", "run.configureTasks", "run.selection"]],
  ["Terminal", ["terminal.new", "terminal.toggle", "-", "run.task", "run.configureTasks"]],
];
