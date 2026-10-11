import { useEffect, useRef, useState } from "react";
import { CircleAlert, Info, Plus, TriangleAlert, X } from "lucide-react";
import { basename } from "../api";
import { debug, useDebug } from "../debug";
import { monaco, openFile } from "../editor";
import { set, useStore, type PanelView } from "../store";
import { closeTerminal, mountTerminal, newTerminal } from "../terminal";

function TerminalPane() {
  const terminals = useStore((s) => s.terminals);
  const active = useStore((s) => s.activeTerminal);
  const host = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (active && host.current) return mountTerminal(active, host.current);
  }, [active]);
  return (
    <div className="term-wrap">
      {terminals.length === 0 ? (
        <div className="panel-empty">
          <button className="btn" onClick={() => void newTerminal()}><Plus size={15} strokeWidth={2.5} /> New terminal</button>
        </div>
      ) : (
        <div className="term-host" ref={host} />
      )}
      {terminals.length > 0 && (
        <div className="term-list">
          {terminals.map((t) => (
            <div key={t.id} className={`term-item${t.id === active ? " is-active" : ""}`} onClick={() => set({ activeTerminal: t.id })} title={t.title}>
              <span className={`term-dot${t.alive ? " is-alive" : ""}`} />
              <span className="tree-name">{t.title}</span>
              <button className="btn-icon" title="Close terminal" onClick={(e) => (e.stopPropagation(), closeTerminal(t.id))}><X size={12} strokeWidth={2.5} /></button>
            </div>
          ))}
          <button className="term-item term-new" onClick={() => void newTerminal()}><Plus size={13} strokeWidth={2.5} /> New</button>
        </div>
      )}
    </div>
  );
}

/** All diagnostics from Monaco's built-in services and from language servers. */
export function useMarkers() {
  const [markers, setMarkers] = useState<monaco.editor.IMarker[]>([]);
  useEffect(() => {
    const read = () => setMarkers(monaco.editor.getModelMarkers({}).filter((m) => m.resource.scheme === "file" && m.severity >= monaco.MarkerSeverity.Info));
    read();
    const sub = monaco.editor.onDidChangeMarkers(read);
    return () => sub.dispose();
  }, []);
  return markers;
}

function Problems() {
  const markers = useMarkers();
  if (markers.length === 0) return <div className="panel-empty">No problems have been detected in the open files.</div>;
  const sorted = [...markers].sort((a, b) => b.severity - a.severity || a.resource.fsPath.localeCompare(b.resource.fsPath) || a.startLineNumber - b.startLineNumber);
  return (
    <div className="panel-scroll">
      {sorted.map((m, i) => (
        <div key={i} className="problem" onClick={() => void openFile(m.resource.fsPath, { line: m.startLineNumber, col: m.startColumn })}>
          {m.severity === monaco.MarkerSeverity.Error ? <CircleAlert size={14} className="sev-error" />
            : m.severity === monaco.MarkerSeverity.Warning ? <TriangleAlert size={14} className="sev-warn" /> : <Info size={14} />}
          <span className="problem-text selectable">{m.message}</span>
          <span className="tree-dim">{m.source}{m.code ? ` ${typeof m.code === "string" ? m.code : m.code.value}` : ""}</span>
          <span className="mono">{basename(m.resource.fsPath)}:{m.startLineNumber}</span>
        </div>
      ))}
    </div>
  );
}

function Log({ text }: { text: string }) {
  const ref = useRef<HTMLPreElement>(null);
  useEffect(() => {
    ref.current?.scrollTo(0, ref.current.scrollHeight);
  }, [text]);
  return <pre className="panel-log selectable" ref={ref}>{text || "Nothing here yet."}</pre>;
}

function DebugConsole() {
  const d = useDebug();
  const [text, setText] = useState("");
  return (
    <div className="console">
      <Log text={d.log} />
      <input className="field" placeholder="Evaluate an expression while paused" value={text} aria-label="Evaluate expression"
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => {
          if (e.key !== "Enter" || !text.trim()) return;
          void debug.evaluate(text.trim());
          setText("");
        }} />
    </div>
  );
}

export function Panel() {
  const panel = useStore((s) => s.panel);
  const output = useStore((s) => s.output);
  const count = useMarkers().length;
  const tabs: [PanelView, string][] = [["terminal", "Terminal"], ["problems", count ? `Problems ${count}` : "Problems"], ["output", "Output"], ["console", "Debug console"]];
  return (
    <div className="bottom">
      <div className="bottom-tabs" role="tablist">
        {tabs.map(([id, label]) => (
          <button key={id} role="tab" aria-selected={panel === id} className={panel === id ? "is-active" : ""} onClick={() => set({ panel: id })}>{label}</button>
        ))}
        <div className="tabs-fill" />
        <button className="btn-icon" title="Hide panel (Ctrl+J)" onClick={() => set({ panelOpen: false })}><X size={14} strokeWidth={2.5} /></button>
      </div>
      {/* The terminal stays mounted so its screen survives switching tabs. */}
      <div className="bottom-body" hidden={panel !== "terminal"}><TerminalPane /></div>
      {panel === "problems" && <div className="bottom-body"><Problems /></div>}
      {panel === "output" && <div className="bottom-body"><Log text={output} /></div>}
      {panel === "console" && <div className="bottom-body"><DebugConsole /></div>}
    </div>
  );
}
