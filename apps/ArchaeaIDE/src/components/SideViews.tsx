// Search, Source Control, Run and Debug and Extensions.
import { useEffect, useRef, useState } from "react";
import {
  ArrowDownToLine, ArrowUpFromLine, Bug, CaseSensitive, Check, ChevronDown, ChevronRight, GitBranch, Minus, Pause, Play,
  Plus, RefreshCw, Regex, Replace, Square, StepForward, ArrowDownToDot, ArrowUpFromDot, Undo2, WholeWord, Trash2,
} from "lucide-react";
import { api, basename, errorText, type FileMatches, type Query } from "../api";
import { run } from "../commands";
import { debug, selectFrame, startDebugging, toggleBreakpoint, useDebug, type Variable } from "../debug";
import { openDiff, openFile, saveAll, syncWithDisk } from "../editor";
import { setExtensionEnabled, useExtensions } from "../extensions";
import { git, headText, recentCommits, refreshGit, useGit, type Change, type Commit } from "../git";
import { ask } from "../overlay";
import { fail, toast, touchFs, useStore } from "../store";
import { switchBranch } from "../actions";
import { shortPath } from "./Explorer";

// ------------------------------------------------------------------ search

export function SearchView() {
  const root = useStore((s) => s.root);
  const [query, setQuery] = useState<Query>({ text: "", regex: false, caseSensitive: false, wholeWord: false });
  const [replacement, setReplacement] = useState("");
  const [showReplace, setShowReplace] = useState(false);
  const [results, setResults] = useState<FileMatches[] | null>(null);
  const [note, setNote] = useState("");
  const [closed, setClosed] = useState<Record<string, boolean>>({});
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    input.current?.focus();
  }, []);

  const search = async (q = query) => {
    if (!root) return setNote("Open a folder to search in it.");
    if (!q.text) return (setResults(null), setNote(""));
    try {
      const r = await api.search(root, q);
      const count = r.files.reduce((n, f) => n + f.matches.length, 0);
      setResults(r.files);
      setNote(count === 0 ? "No results." : `${count}${r.truncated ? "+" : ""} result${count === 1 ? "" : "s"} in ${r.files.length} file${r.files.length === 1 ? "" : "s"}`);
    } catch (e) {
      setResults(null);
      setNote(errorText(e));
    }
  };
  const flip = (key: "regex" | "caseSensitive" | "wholeWord") => {
    const next = { ...query, [key]: !query[key] };
    setQuery(next);
    void search(next);
  };
  const replaceAll = async () => {
    if (!root || !results?.length) return;
    const count = results.reduce((n, f) => n + f.matches.length, 0);
    const answer = await ask("Replace in files?", `Replace ${count} occurrence${count === 1 ? "" : "s"} of "${query.text}" with "${replacement}" in ${results.length} file${results.length === 1 ? "" : "s"}? Open files are saved first.`, [
      { label: "Replace", value: "yes", kind: "primary" },
      { label: "Cancel", value: "no" },
    ]);
    if (answer !== "yes") return;
    try {
      await saveAll();
      const done = await api.replaceAll(root, query, replacement, results.map((f) => f.path));
      await syncWithDisk();
      touchFs();
      toast("ok", `Replaced ${done} occurrence${done === 1 ? "" : "s"}`);
      await search();
    } catch (e) {
      fail("Replace failed")(e);
    }
  };

  return (
    <>
      <div className="side-head"><span>Search</span></div>
      <div className="side-form">
        <div className="field-row">
          <button className="btn-icon" title="Toggle replace" onClick={() => setShowReplace(!showReplace)}>
            {showReplace ? <ChevronDown size={14} strokeWidth={2.5} /> : <ChevronRight size={14} strokeWidth={2.5} />}
          </button>
          <input ref={input} className="field" placeholder="Search" value={query.text} aria-label="Search"
            onChange={(e) => setQuery({ ...query, text: e.target.value })}
            onKeyDown={(e) => e.key === "Enter" && void search()} />
        </div>
        <div className="field-row toggles">
          <button className="btn-icon" aria-pressed={query.caseSensitive} title="Match case" onClick={() => flip("caseSensitive")}><CaseSensitive size={16} /></button>
          <button className="btn-icon" aria-pressed={query.wholeWord} title="Match whole word" onClick={() => flip("wholeWord")}><WholeWord size={16} /></button>
          <button className="btn-icon" aria-pressed={query.regex} title="Use regular expression" onClick={() => flip("regex")}><Regex size={15} /></button>
          <span className="side-note">{note}</span>
        </div>
        {showReplace && (
          <div className="field-row">
            <input className="field" placeholder="Replace with" value={replacement} aria-label="Replace with" onChange={(e) => setReplacement(e.target.value)} />
            <button className="btn-icon" title="Replace all" disabled={!results?.length} onClick={() => void replaceAll()}><Replace size={15} strokeWidth={2.25} /></button>
          </div>
        )}
      </div>
      <div className="side-scroll">
        {results?.map((f) => (
          <div key={f.path}>
            <div className="tree-row is-file" onClick={() => setClosed({ ...closed, [f.path]: !closed[f.path] })} title={f.path}>
              {closed[f.path] ? <ChevronRight size={14} strokeWidth={2.5} /> : <ChevronDown size={14} strokeWidth={2.5} />}
              <span className="tree-name">{basename(f.path)}</span>
              <span className="tree-dim">{shortPath(root, f.path).replace(/[^\\]*$/, "")}</span>
              <span className="tree-mark">{f.matches.length}</span>
            </div>
            {!closed[f.path] && f.matches.map((m, i) => (
              <div key={i} className="tree-row match" style={{ paddingLeft: 30 }}
                onClick={() => void openFile(f.path, { line: m.line, col: m.col, len: m.len })}>
                <span className="tree-name">
                  {m.text.slice(Math.max(0, m.col - 1 - 24), m.col - 1).trimStart()}
                  <mark>{m.text.slice(m.col - 1, m.col - 1 + m.len)}</mark>
                  {m.text.slice(m.col - 1 + m.len, m.col + m.len + 80)}
                </span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </>
  );
}

// ----------------------------------------------------------- source control

const label: Record<string, string> = { M: "Modified", A: "Added", D: "Deleted", R: "Renamed", "?": "Untracked", U: "Conflict", C: "Copied" };

export function GitView() {
  const root = useStore((s) => s.root);
  const state = useGit();
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [commits, setCommits] = useState<Commit[]>([]);
  useEffect(() => void refreshGit(), []);
  useEffect(() => void (state.repo ? recentCommits().then(setCommits) : setCommits([])), [state]);

  const act = async (title: string, fn: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await fn();
    } catch (e) {
      fail(title)(e);
    }
    await syncWithDisk();
    touchFs();
    await refreshGit();
    setBusy(false);
  };
  if (!root) return <div className="side-empty"><p>Open a folder to use source control.</p></div>;
  if (!state.available) return <div className="side-empty"><p>Git was not found on PATH. Install Git for Windows, then restart ArchaeaIDE.</p></div>;
  if (!state.repo)
    return (
      <div className="side-empty">
        <p>This folder is not a Git repository.</p>
        <button className="btn btn-primary btn-wide" onClick={() => run("git.init")}>Initialize repository</button>
      </div>
    );

  const staged = state.changes.filter((c) => c.index !== " " && c.index !== "?");
  const unstaged = state.changes.filter((c) => c.tree !== " " || c.index === "?");
  const diff = async (c: Change) => {
    if (c.tree === "D" || c.index === "D") return toast("info", `${c.rel} was deleted.`);
    await openDiff(c.path, c.index === "?" || c.index === "A" ? "" : await headText(c.rel), "changes");
  };
  const discard = async (c: Change) => {
    const untracked = c.index === "?";
    const answer = await ask(`Discard changes to ${basename(c.path)}?`, untracked ? "This file is not tracked by Git. It will be moved to the Recycle Bin." : "Your uncommitted changes to this file will be lost. This cannot be undone.", [
      { label: "Discard", value: "yes", kind: "danger" },
      { label: "Cancel", value: "no" },
    ]);
    if (answer !== "yes") return;
    await act("Could not discard", () => (untracked ? api.trash(c.path) : git(["restore", "--source=HEAD", "--staged", "--worktree", "--", c.rel])));
  };
  const commit = () =>
    act("Commit failed", async () => {
      if (!message.trim()) throw new Error("Write a commit message first.");
      // With nothing staged, commit everything — the common case for small projects.
      if (staged.length === 0) await git(["add", "-A"]);
      await git(["commit", "-m", message.trim()]);
      setMessage("");
      toast("ok", "Committed");
    });
  const row = (c: Change, isStaged: boolean) => {
    const code = isStaged ? c.index : c.index === "?" ? "?" : c.tree;
    return (
      <div key={(isStaged ? "s" : "u") + c.rel} className="tree-row is-file" title={`${c.rel} — ${label[code] ?? code}`} onClick={() => void diff(c)}>
        <span className="tree-name">{basename(c.path)}</span>
        <span className="tree-dim">{c.rel.replace(/[^/]*$/, "")}</span>
        <span className="row-tools">
          {!isStaged && <button className="btn-icon" title="Discard changes" onClick={(e) => (e.stopPropagation(), void discard(c))}><Undo2 size={13} strokeWidth={2.5} /></button>}
          <button className="btn-icon" title={isStaged ? "Unstage" : "Stage"}
            onClick={(e) => (e.stopPropagation(), void act("Git failed", () => git(isStaged ? ["restore", "--staged", "--", c.rel] : ["add", "--", c.rel])))}>
            {isStaged ? <Minus size={13} strokeWidth={2.5} /> : <Plus size={13} strokeWidth={2.5} />}
          </button>
        </span>
        <span className="tree-mark">{code === "?" ? "U" : code}</span>
      </div>
    );
  };

  return (
    <>
      <div className="side-head">
        <span>Source control</span>
        <button className="btn-icon" title="Pull" disabled={busy} onClick={() => void act("Pull failed", () => git(["pull", "--ff-only"], { timeout: 120 }))}><ArrowDownToLine size={15} strokeWidth={2.25} /></button>
        <button className="btn-icon" title="Push" disabled={busy} onClick={() => void act("Push failed", () => git(state.upstream ? ["push"] : ["push", "-u", "origin", state.branch], { timeout: 120 }).then(() => toast("ok", "Pushed")))}><ArrowUpFromLine size={15} strokeWidth={2.25} /></button>
        <button className="btn-icon" title="Refresh" onClick={() => void refreshGit()}><RefreshCw size={14} strokeWidth={2.25} /></button>
      </div>
      <div className="side-form">
        <button className="branch" onClick={() => void switchBranch()} title="Switch branch">
          <GitBranch size={14} strokeWidth={2.5} /> <span>{state.branch}</span>
          {(state.ahead > 0 || state.behind > 0) && <span className="mono">↑{state.ahead} ↓{state.behind}</span>}
        </button>
        <textarea className="field" rows={3} placeholder="Commit message" value={message} aria-label="Commit message"
          onChange={(e) => setMessage(e.target.value)}
          onKeyDown={(e) => e.ctrlKey && e.key === "Enter" && void commit()} />
        <button className="btn btn-primary btn-wide" disabled={busy || state.changes.length === 0} onClick={() => void commit()}>
          <Check size={15} strokeWidth={2.75} /> {staged.length ? `Commit ${staged.length} staged` : "Commit all"}
        </button>
      </div>
      <div className="side-scroll">
        {staged.length > 0 && (
          <>
            <div className="side-sub"><span>Staged</span><span className="tree-mark">{staged.length}</span></div>
            {staged.map((c) => row(c, true))}
          </>
        )}
        <div className="side-sub">
          <span>Changes</span>
          {unstaged.length > 0 && <button className="btn-icon" title="Stage all" onClick={() => void act("Git failed", () => git(["add", "-A"]))}><Plus size={13} strokeWidth={2.5} /></button>}
          <span className="tree-mark">{unstaged.length}</span>
        </div>
        {unstaged.map((c) => row(c, false))}
        {state.changes.length === 0 && <p className="side-note pad">No changes.</p>}
        {commits.length > 0 && <div className="side-sub"><span>Recent commits</span></div>}
        {commits.map((c) => (
          <div key={c.hash} className="tree-row commit" title={`${c.hash} · ${c.author} · ${c.when}`}>
            <span className="tree-name">{c.subject}</span>
            <span className="tree-dim">{c.when.replace(" ago", "")}</span>
          </div>
        ))}
      </div>
    </>
  );
}

// -------------------------------------------------------------- debugging

function VariableRow({ v, depth }: { v: Variable; depth: number }) {
  const [children, setChildren] = useState<Variable[] | null>(null);
  return (
    <>
      <div className="tree-row var" style={{ paddingLeft: 8 + depth * 14 }} title={`${v.name} = ${v.value}`}
        onClick={() => v.ref && (children ? setChildren(null) : void debug.variables(v.ref).then(setChildren).catch(() => setChildren([])))}>
        {v.ref ? (children ? <ChevronDown size={13} strokeWidth={2.5} /> : <ChevronRight size={13} strokeWidth={2.5} />) : <span className="tree-leaf" />}
        <span className="var-name">{v.name}</span>
        <span className="var-value">{v.value}</span>
      </div>
      {children?.slice(0, 200).map((c, i) => <VariableRow key={i + c.name} v={c} depth={depth + 1} />)}
    </>
  );
}

function ScopeRows({ name, scopeRef, open }: { name: string; scopeRef: string; open: boolean }) {
  const [vars, setVars] = useState<Variable[] | null>(null);
  useEffect(() => {
    if (open) void debug.variables(scopeRef).then(setVars).catch(() => setVars([]));
  }, [scopeRef, open]);
  return (
    <>
      <div className="tree-row scope" onClick={() => (vars ? setVars(null) : void debug.variables(scopeRef).then(setVars))}>
        {vars ? <ChevronDown size={13} strokeWidth={2.5} /> : <ChevronRight size={13} strokeWidth={2.5} />}
        <span className="tree-name">{name}</span>
      </div>
      {vars?.slice(0, 300).map((v, i) => <VariableRow key={i + v.name} v={v} depth={1} />)}
    </>
  );
}

export function DebugView() {
  const d = useDebug();
  const breakpoints = useStore((s) => s.breakpoints);
  const running = useStore((s) => s.running);
  const paused = d.status === "paused";
  const active = d.status !== "idle";
  return (
    <>
      <div className="side-head"><span>Run and debug</span></div>
      <div className="side-form">
        <div className="debug-bar">
          {!active ? (
            <>
              <button className="btn btn-primary" onClick={() => run("run.run")} disabled={running}><Play size={14} strokeWidth={2.75} /> Run</button>
              <button className="btn" onClick={() => void startDebugging()} disabled={running}><Bug size={14} strokeWidth={2.5} /> Debug</button>
            </>
          ) : (
            <>
              <button className="btn-icon" title="Continue (F6)" disabled={!paused} onClick={debug.resume}><Play size={15} strokeWidth={2.5} /></button>
              <button className="btn-icon" title="Pause" disabled={paused} onClick={debug.pause}><Pause size={15} strokeWidth={2.5} /></button>
              <button className="btn-icon" title="Step over (F10)" disabled={!paused} onClick={debug.next}><StepForward size={15} strokeWidth={2.5} /></button>
              <button className="btn-icon" title="Step into (F11)" disabled={!paused} onClick={debug.stepIn}><ArrowDownToDot size={15} strokeWidth={2.5} /></button>
              <button className="btn-icon" title="Step out (Shift+F11)" disabled={!paused} onClick={debug.stepOut}><ArrowUpFromDot size={15} strokeWidth={2.5} /></button>
              <button className="btn-icon is-stop" title="Stop (Shift+F5)" onClick={debug.stop}><Square size={13} strokeWidth={3} /></button>
            </>
          )}
        </div>
        <span className="side-note">
          {d.status === "idle" ? "Click beside a line number to set a breakpoint, then press Debug. Python and Node.js are supported."
            : d.status === "starting" ? "Starting…" : paused ? `Paused: ${d.reason}` : "Running"}
        </span>
      </div>
      <div className="side-scroll">
        <div className="side-sub"><span>Variables</span></div>
        {paused ? d.scopes.map((s, i) => <ScopeRows key={`${d.stops}:${d.frame}:${s.ref}`} name={s.name} scopeRef={s.ref} open={i === 0} />)
          : <p className="side-note pad">Shown when the program is paused.</p>}
        <div className="side-sub"><span>Call stack</span></div>
        {d.frames.map((f) => (
          <div key={f.id} className={`tree-row is-file${f.id === d.frame ? " is-active" : ""}${f.path ? "" : " is-dim"}`} onClick={() => void selectFrame(f)}>
            <span className="tree-name">{f.name}</span>
            <span className="tree-dim">{f.path ? `${basename(f.path)}:${f.line}` : ""}</span>
          </div>
        ))}
        <div className="side-sub"><span>Breakpoints</span></div>
        {Object.entries(breakpoints).flatMap(([path, lines]) =>
          lines.map((line) => (
            <div key={path + line} className="tree-row is-file" onClick={() => void openFile(path, { line })}>
              <span className="bp-dot" />
              <span className="tree-name">{basename(path)}</span>
              <span className="tree-dim">line {line}</span>
              <span className="row-tools">
                <button className="btn-icon" title="Remove" onClick={(e) => (e.stopPropagation(), toggleBreakpoint(path, line))}><Trash2 size={13} strokeWidth={2.25} /></button>
              </span>
            </div>
          ))
        )}
        {Object.keys(breakpoints).length === 0 && <p className="side-note pad">No breakpoints.</p>}
      </div>
    </>
  );
}

// -------------------------------------------------------------- extensions

export function ExtensionsView() {
  const list = useExtensions();
  const enabled = useStore((s) => s.settings.extensions);
  return (
    <>
      <div className="side-head">
        <span>Extensions</span>
        <button className="btn-icon" title="Reload extensions" onClick={() => run("ext.reload")}><RefreshCw size={14} strokeWidth={2.25} /></button>
      </div>
      <div className="side-scroll">
        {list.map((e) => (
          <div key={e.id} className="ext">
            <div className="ext-head">
              <span className="ext-name">{e.name}</span>
              <button className="switch" role="switch" aria-checked={enabled.includes(e.id)} aria-label={`Enable ${e.name}`}
                disabled={!!e.error} onClick={() => void setExtensionEnabled(e.id, !enabled.includes(e.id))} />
            </div>
            <div className="side-note">{e.error ?? e.description}</div>
            <div className="mono ext-meta">{e.id}{e.version && ` · ${e.version}`}</div>
          </div>
        ))}
        {list.length === 0 && <p className="side-note pad">No extensions installed.</p>}
        <div className="side-form">
          <p className="side-note">
            An extension is a folder with an <b>extension.json</b> and an optional <b>main.js</b>. Scripts run in a sandbox and
            can add commands and snippets. Each one stays off until you switch it on.
          </p>
          <button className="btn btn-wide" onClick={() => run("ext.sample")}>Create sample extension</button>
          <button className="btn btn-wide" onClick={() => run("ext.folder")}>Open extensions folder</button>
        </div>
      </div>
    </>
  );
}
