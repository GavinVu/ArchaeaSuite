import { useEffect, useRef, useState } from "react";
import {
  Blocks, Bot, Bug, CircleAlert, Copy, Files, GitBranch, Minus, Play, Search, Settings as SettingsIcon, Square,
  TriangleAlert, X,
} from "lucide-react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { api, basename, dirname } from "./api";
import { menus, registerCommands, switchBranch } from "./actions";
import { allCommands, installKeys, keyFor, run } from "./commands";
import { debug, useDebug } from "./debug";
import { closeAllTabs, monaco, openFile, openSpecial } from "./editor";
import { loadExtensions } from "./extensions";
import { useGit } from "./git";
import { servers, startServer, stopAllServers, useServerStatus } from "./lsp";
import { freezeSession, get, loadConfig, onChange, saveSession, set, useStore, type SideView } from "./store";
import { stopRun } from "./terminal";
import { openFolder, refreshFromDisk } from "./workspace";
import { EditorArea } from "./components/EditorArea";
import { Explorer } from "./components/Explorer";
import { Overlays } from "./components/Overlays";
import { Panel, useMarkers } from "./components/Panel";
import { AiView } from "./components/AiView";
import { DebugView, ExtensionsView, GitView, SearchView } from "./components/SideViews";

let booted = false;
async function boot() {
  if (booted) return;
  booted = true;
  registerCommands();
  installKeys();
  const session = await loadConfig();
  monaco.editor.setTheme(get().settings.theme === "dark" ? "archaea-dark" : "archaea-light");
  void document.fonts.ready.then(() => monaco.editor.remeasureFonts());
  await loadExtensions();
  const startup = await api.startupPath();
  if (startup) {
    const stat = await api.stat(startup);
    await openFolder(stat.dir ? startup : dirname(startup));
    if (!stat.dir) await openFile(startup);
  } else if (session.root) {
    await openFolder(session.root, session.files, session.active);
  }
  if (get().groups.every((g) => g.tabs.length === 0)) openSpecial("welcome");

  const win = getCurrentWindow();
  window.addEventListener("focus", () => void refreshFromDisk());
  // Closing asks about unsaved files first; closeAllTabs returns false on Cancel.
  void win.onCloseRequested(async (event) => {
    event.preventDefault();
    await freezeSession(true);
    if (Object.keys(get().dirty).length > 0 && !(await closeAllTabs())) return void freezeSession(false);
    stopAllServers();
    await win.destroy();
  });
  // The window title and language servers follow the open folder.
  let root: string | null | undefined;
  const sync = () => {
    if (get().root === root) return;
    if (root !== undefined) stopAllServers();
    root = get().root;
    void win.setTitle(root ? `${basename(root)} — ArchaeaIDE` : "ArchaeaIDE");
  };
  sync();
  onChange(sync);
}

function MenuBar() {
  const [open, setOpen] = useState<string | null>(null);
  useStore((s) => s.keys);
  useEffect(() => {
    if (!open) return;
    const close = () => setOpen(null);
    window.addEventListener("mousedown", close);
    window.addEventListener("blur", close);
    return () => {
      window.removeEventListener("mousedown", close);
      window.removeEventListener("blur", close);
    };
  }, [open]);
  const commands = new Map(allCommands().map((c) => [c.id, c]));
  return (
    <nav className="menubar" onMouseDown={(e) => e.stopPropagation()}>
      {menus.map(([name, ids]) => (
        <div key={name} className="menubar-item">
          <button aria-expanded={open === name} onClick={() => setOpen(open === name ? null : name)} onMouseEnter={() => open && setOpen(name)}>
            {name}
          </button>
          {open === name && (
            <div className="menu" role="menu">
              {ids.map((id, i) => {
                const c = commands.get(id);
                if (!c) return <div key={i} className="menu-sep" />;
                return (
                  <button key={id} role="menuitem" onClick={() => (setOpen(null), void c.run())}>
                    <span>{c.title}</span>
                    {keyFor(c) && <kbd>{keyFor(c)}</kbd>}
                  </button>
                );
              })}
            </div>
          )}
        </div>
      ))}
    </nav>
  );
}

function TitleBar() {
  const root = useStore((s) => s.root);
  const running = useStore((s) => s.running);
  const debugging = useDebug().status !== "idle";
  const dark = useStore((s) => s.settings.theme === "dark");
  const [maximized, setMaximized] = useState(false);
  const win = getCurrentWindow();
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    const sync = () => win.isMaximized().then(setMaximized).catch(() => {});
    void sync();
    void win.onResized(sync).then((fn) => (unlisten = fn));
    return () => unlisten?.();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <header className="titlebar">
      <img className="titlebar-mark" src={dark ? "/brand/mark-inverse.png" : "/brand/mark.png"} alt="" data-tauri-drag-region />
      <MenuBar />
      <div className="titlebar-drag" data-tauri-drag-region>
        <span className="titlebar-title">{root ? basename(root) : "ArchaeaIDE"}</span>
      </div>
      <div className="run-controls">
        {running || debugging ? (
          <button className="run-btn is-stop" title="Stop (Shift+F5)" onClick={() => (debugging ? debug.stop() : stopRun())}>
            <Square size={12} strokeWidth={3} /> Stop
          </button>
        ) : (
          <>
            <button className="run-btn" title="Run the active file (F5)" onClick={() => run("run.run")}>
              <Play size={13} strokeWidth={3} /> Run
            </button>
            <button className="run-btn is-quiet" title="Debug the active file (F6)" aria-label="Debug" onClick={() => run("run.debug")}>
              <Bug size={14} strokeWidth={2.5} />
            </button>
          </>
        )}
      </div>
      <div className="window-controls">
        <button onClick={() => void win.minimize()} aria-label="Minimize" tabIndex={-1}><Minus size={15} strokeWidth={2.25} /></button>
        <button onClick={() => void win.toggleMaximize()} aria-label={maximized ? "Restore" : "Maximize"} tabIndex={-1}>
          {maximized ? <Copy size={12} strokeWidth={2.5} /> : <Square size={12} strokeWidth={2.5} />}
        </button>
        <button className="is-close" onClick={() => void win.close()} aria-label="Close" tabIndex={-1}><X size={16} strokeWidth={2.25} /></button>
      </div>
    </header>
  );
}

const views: [SideView, string, typeof Files][] = [
  ["explorer", "Explorer", Files],
  ["search", "Search", Search],
  ["git", "Source control", GitBranch],
  ["debug", "Run and debug", Bug],
  ["extensions", "Extensions", Blocks],
  ["ai", "Local AI", Bot],
];

function ActivityBar() {
  const side = useStore((s) => s.side);
  const open = useStore((s) => s.sideOpen);
  const changes = useGit().changes.length;
  return (
    <aside className="activity">
      {views.map(([id, label, Icon]) => (
        <button key={id} title={label} aria-label={label} aria-pressed={open && side === id}
          onClick={() => set(open && side === id ? { sideOpen: false } : { side: id, sideOpen: true })}>
          <Icon size={20} strokeWidth={2} />
          {id === "git" && changes > 0 && <span className="activity-badge">{changes > 99 ? "99+" : changes}</span>}
        </button>
      ))}
      <div className="tabs-fill" />
      <button title="Settings" aria-label="Settings" onClick={() => run("file.settings")}><SettingsIcon size={20} strokeWidth={2} /></button>
    </aside>
  );
}

function StatusBar() {
  const cursor = useStore((s) => s.cursor);
  const language = useStore((s) => s.language);
  const tabSize = useStore((s) => s.settings.tabSize);
  const git = useGit();
  const markers = useMarkers();
  const server = useServerStatus(language);
  const errors = markers.filter((m) => m.severity === monaco.MarkerSeverity.Error).length;
  const warnings = markers.filter((m) => m.severity === monaco.MarkerSeverity.Warning).length;
  const builtIn = ["javascript", "typescript", "json", "css", "scss", "less", "html"].includes(language);
  const serverLabel = builtIn ? "IntelliSense: built in"
    : server.status === "running" ? `Language server: ${basename(server.name).replace(/\.(exe|cmd)$/i, "")}`
    : server.status === "starting" ? "Language server: starting…"
    : server.status === "manual" ? "Language server: click to start"
    : server.status === "missing" ? "Language server: not installed"
    : server.status === "stopped" ? "Language server: stopped"
    : servers[language] ? "Language server: off" : "";
  return (
    <footer className="statusbar">
      {git.repo && <button onClick={() => void switchBranch()} title="Switch branch"><GitBranch size={12} strokeWidth={2.5} /> {git.branch}</button>}
      <button onClick={() => run("view.problems")} title="Problems">
        <CircleAlert size={12} strokeWidth={2.5} /> {errors} <TriangleAlert size={12} strokeWidth={2.5} /> {warnings}
      </button>
      <div className="tabs-fill" />
      {serverLabel && (
        <button title={builtIn ? "Provided by the editor" : "Start or restart the language server"}
          onClick={() => !builtIn && (server.status === "running" ? run("lsp.restart") : void startServer(language, true))}>
          {serverLabel}
        </button>
      )}
      {cursor && <button onClick={() => run("go.line")} title="Go to line">Ln {cursor.line}, Col {cursor.col}{cursor.selected > 0 && ` (${cursor.selected} selected)`}</button>}
      {cursor && <span>Spaces: {tabSize}</span>}
      {language && <span>{language}</span>}
    </footer>
  );
}

/** A drag handle that resizes the side bar or the bottom panel. */
function Resizer({ axis }: { axis: "x" | "y" }) {
  const start = useRef(0);
  return (
    <div
      className={`resizer resizer-${axis}`}
      onMouseDown={(e) => {
        e.preventDefault();
        const from = axis === "x" ? get().sideWidth : get().panelHeight;
        start.current = axis === "x" ? e.clientX : e.clientY;
        const move = (m: MouseEvent) => {
          if (axis === "x") set({ sideWidth: Math.max(170, Math.min(640, from + m.clientX - start.current)) });
          else set({ panelHeight: Math.max(90, Math.min(window.innerHeight - 220, from - (m.clientY - start.current))) });
        };
        const up = () => {
          window.removeEventListener("mousemove", move);
          window.removeEventListener("mouseup", up);
          document.body.classList.remove("is-resizing");
          saveSession();
        };
        document.body.classList.add("is-resizing");
        window.addEventListener("mousemove", move);
        window.addEventListener("mouseup", up);
      }}
    />
  );
}

export function App() {
  const side = useStore((s) => s.side);
  const sideOpen = useStore((s) => s.sideOpen);
  const sideWidth = useStore((s) => s.sideWidth);
  const panelOpen = useStore((s) => s.panelOpen);
  const panelHeight = useStore((s) => s.panelHeight);
  const ready = useStore((s) => s.info !== null);
  useEffect(() => void boot(), []);
  if (!ready) return null;
  return (
    <div
      className="ide"
      onContextMenu={(e) => !(e.target as HTMLElement).closest(".monaco-editor, .xterm, input, textarea, .selectable") && e.preventDefault()}
    >
      <TitleBar />
      <div className="ide-body">
        <ActivityBar />
        {sideOpen && (
          <>
            <div className="side" style={{ width: sideWidth }}>
              {side === "explorer" && <Explorer />}
              {side === "search" && <SearchView />}
              {side === "git" && <GitView />}
              {side === "debug" && <DebugView />}
              {side === "extensions" && <ExtensionsView />}
              {side === "ai" && <AiView />}
            </div>
            <Resizer axis="x" />
          </>
        )}
        <div className="work">
          <EditorArea />
          {panelOpen && <Resizer axis="y" />}
          <div style={{ height: panelHeight }} hidden={!panelOpen} className="bottom-slot"><Panel /></div>
        </div>
      </div>
      <StatusBar />
      <Overlays />
    </div>
  );
}
