import { useEffect, useState } from "react";
import { ChevronDown, ChevronRight, FilePlus2, FolderPlus, RefreshCw } from "lucide-react";
import { revealItemInDir } from "@tauri-apps/plugin-opener";
import { api, basename, relative, type Entry } from "../api";
import { openFile } from "../editor";
import { useGit } from "../git";
import { openMenu } from "../overlay";
import { touchFs, useStore } from "../store";
import { newTerminal } from "../terminal";
import { createIn, deletePath, renamePath } from "../workspace";
import { run } from "../commands";

const expanded = new Set<string>();

function Folder({ path, depth, status }: { path: string; depth: number; status: Map<string, string> }) {
  const fsVersion = useStore((s) => s.fsVersion);
  const active = useStore((s) => {
    const g = s.groups.find((x) => x.id === s.activeGroup);
    return g?.tabs.find((t) => t.id === g.active)?.path?.toLowerCase() ?? "";
  });
  const [entries, setEntries] = useState<Entry[]>([]);
  const [, redraw] = useState(0);

  useEffect(() => {
    let live = true;
    api.listDir(path).then((list) => live && setEntries(list)).catch(() => live && setEntries([]));
    return () => void (live = false);
  }, [path, fsVersion]);

  const menu = (e: React.MouseEvent, entry: Entry) => {
    e.preventDefault();
    e.stopPropagation();
    const dir = entry.dir ? entry.path : path;
    openMenu(e.clientX, e.clientY, [
      { label: "New File…", run: () => (expanded.add(dir.toLowerCase()), void createIn(dir, "file")) },
      { label: "New Folder…", run: () => (expanded.add(dir.toLowerCase()), void createIn(dir, "folder")) },
      { separator: true, label: "" },
      { label: "Rename…", run: () => void renamePath(entry.path) },
      { label: "Delete", run: () => void deletePath(entry.path, entry.dir) },
      { separator: true, label: "" },
      { label: "Copy Path", run: () => void navigator.clipboard.writeText(entry.path) },
      { label: "Reveal in File Explorer", run: () => void revealItemInDir(entry.path) },
      { label: "Open Terminal Here", run: () => void newTerminal(dir) },
    ]);
  };

  return (
    <>
      {entries.map((entry) => {
        const key = entry.path.toLowerCase();
        const open = entry.dir && expanded.has(key);
        const mark = status.get(key);
        return (
          <div key={entry.path}>
            <div
              className={`tree-row${key === active ? " is-active" : ""}${mark ? " is-changed" : ""}`}
              style={{ paddingLeft: 8 + depth * 14 }}
              title={entry.path}
              onClick={() => {
                if (!entry.dir) return void openFile(entry.path);
                if (open) expanded.delete(key);
                else expanded.add(key);
                redraw((n) => n + 1);
              }}
              onContextMenu={(e) => menu(e, entry)}
            >
              {entry.dir ? (open ? <ChevronDown size={14} strokeWidth={2.5} /> : <ChevronRight size={14} strokeWidth={2.5} />) : <span className="tree-leaf" />}
              <span className="tree-name">{entry.name}</span>
              {mark && <span className="tree-mark">{mark}</span>}
            </div>
            {open && <Folder path={entry.path} depth={depth + 1} status={status} />}
          </div>
        );
      })}
    </>
  );
}

export function Explorer() {
  const root = useStore((s) => s.root);
  const git = useGit();
  if (!root)
    return (
      <div className="side-empty">
        <p>No folder is open.</p>
        <button className="btn btn-primary btn-wide" onClick={() => run("file.openFolder")}>Open folder</button>
        <button className="btn btn-wide" onClick={() => run("file.newProject")}>New project</button>
      </div>
    );
  // One letter per changed file (M, A, D, U for untracked), and a dot on the folders above it.
  const status = new Map<string, string>();
  for (const c of git.changes) {
    const letter = c.index === "?" ? "U" : c.tree !== " " ? c.tree : c.index;
    status.set(c.path.toLowerCase(), letter);
    let dir = c.path.toLowerCase();
    while ((dir = dir.slice(0, dir.lastIndexOf("\\"))).length > root.length) if (!status.has(dir)) status.set(dir, "•");
  }
  return (
    <>
      <div className="side-head">
        <span title={root}>{basename(root)}</span>
        <button className="btn-icon" title="New file" onClick={() => void createIn(root, "file")}><FilePlus2 size={15} strokeWidth={2.25} /></button>
        <button className="btn-icon" title="New folder" onClick={() => void createIn(root, "folder")}><FolderPlus size={15} strokeWidth={2.25} /></button>
        <button className="btn-icon" title="Refresh" onClick={touchFs}><RefreshCw size={14} strokeWidth={2.25} /></button>
      </div>
      <div
        className="side-scroll tree"
        onContextMenu={(e) => {
          e.preventDefault();
          openMenu(e.clientX, e.clientY, [
            { label: "New File…", run: () => void createIn(root, "file") },
            { label: "New Folder…", run: () => void createIn(root, "folder") },
            { label: "Reveal in File Explorer", run: () => void revealItemInDir(root) },
          ]);
        }}
      >
        <Folder path={root} depth={0} status={status} />
      </div>
    </>
  );
}

export const shortPath = (root: string | null, path: string) => (root ? relative(root, path) : path);
