import { useEffect, useState } from "react";
import { FolderOpen, FilePlus2, Moon, RotateCcw, Sun, SquareTerminal } from "lucide-react";
import { basename } from "../api";
import { allCommands, chordOf, keyFor, run } from "../commands";
import { saveKeys, saveSettings, useStore, type Settings } from "../store";
import { openFolder } from "../workspace";

export function Welcome() {
  const recent = useStore((s) => s.recent);
  const root = useStore((s) => s.root);
  const info = useStore((s) => s.info);
  const dark = useStore((s) => s.settings.theme === "dark");
  return (
    <div className="page-scroll">
      <div className="welcome">
        <div className="welcome-brand">
          <img src={dark ? "/brand/mark-inverse.png" : "/brand/mark.png"} alt="" />
          <div>
            <h1 className="welcome-title">Archaea<b>IDE</b></h1>
            <div className="brand-sub">Code editor · v{info?.version} · {info?.arch}</div>
          </div>
        </div>
        <div className="welcome-actions">
          <button className="btn btn-primary" onClick={() => run("file.newProject")}>
            <FilePlus2 size={16} strokeWidth={2.5} /> New project
          </button>
          <button className="btn" onClick={() => run("file.openFolder")}>
            <FolderOpen size={16} strokeWidth={2.5} /> Open folder
          </button>
          <button className="btn" onClick={() => run("terminal.new")}>
            <SquareTerminal size={16} strokeWidth={2.5} /> Terminal
          </button>
        </div>
        {recent.filter((r) => r !== root).length > 0 && (
          <>
            <h2 className="sub-title">Recent</h2>
            <div className="recent">
              {recent.filter((r) => r !== root).slice(0, 8).map((r) => (
                <button key={r} className="recent-item" onClick={() => void openFolder(r)}>
                  <span>{basename(r)}</span>
                  <span className="mono">{r}</span>
                </button>
              ))}
            </div>
          </>
        )}
        <h2 className="sub-title">Keys worth knowing</h2>
        <div className="keys-grid">
          {["view.palette", "go.file", "run.run", "run.debug", "terminal.toggle", "edit.findInFiles", "view.split", "run.task"].map((id) => {
            const c = allCommands().find((x) => x.id === id);
            return c ? (
              <button key={id} className="keys-row" onClick={() => run(id)}>
                <span>{c.title.replace("…", "")}</span>
                <kbd>{keyFor(c)}</kbd>
              </button>
            ) : null;
          })}
        </div>
      </div>
    </div>
  );
}

function Switch({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return <button className="switch" role="switch" aria-checked={checked} aria-label={label} onClick={() => onChange(!checked)} />;
}

function Row({ label, help, children }: { label: string; help: string; children: React.ReactNode }) {
  return (
    <div className="setting">
      <div className="setting-text">
        <div className="setting-label">{label}</div>
        <div className="setting-help">{help}</div>
      </div>
      {children}
    </div>
  );
}

function NumberField({ value, min, max, onChange, label }: { value: number; min: number; max: number; onChange: (v: number) => void; label: string }) {
  return (
    <input
      className="field field-num"
      type="number"
      aria-label={label}
      value={value}
      min={min}
      max={max}
      onChange={(e) => {
        const v = Number(e.target.value);
        if (v >= min && v <= max) onChange(v);
      }}
    />
  );
}

export function SettingsPage() {
  const s = useStore((st) => st.settings);
  const toggle = (key: keyof Settings, label: string, help: string) => (
    <Row label={label} help={help}>
      <Switch checked={s[key] as boolean} label={label} onChange={(v) => saveSettings({ [key]: v })} />
    </Row>
  );
  return (
    <div className="page-scroll">
      <div className="page-narrow">
        <h1 className="page-title">Settings</h1>
        <h2 className="sub-title">Appearance</h2>
        <div className="panel">
          <Row label="Theme" help="Dark or light, matching the rest of ArchaeaSuite.">
            <div className="segmented" role="radiogroup" aria-label="Theme">
              {([["dark", "Dark", Moon], ["light", "Light", Sun]] as const).map(([id, label, Icon]) => (
                <button key={id} role="radio" aria-checked={s.theme === id} onClick={() => saveSettings({ theme: id })}>
                  <Icon size={14} strokeWidth={2.5} aria-hidden="true" />
                  {label}
                </button>
              ))}
            </div>
          </Row>
          <Row label="Font size" help="Editor text size in pixels. The terminal follows it.">
            <NumberField label="Font size" value={s.fontSize} min={8} max={32} onChange={(v) => saveSettings({ fontSize: v })} />
          </Row>
          {toggle("minimap", "Minimap", "Show an overview of the file at the right edge of the editor.")}
          {toggle("wordWrap", "Word wrap", "Wrap long lines instead of scrolling sideways.")}
        </div>
        <h2 className="sub-title">Editing</h2>
        <div className="panel">
          <Row label="Tab size" help="Width of an indent, in spaces.">
            <NumberField label="Tab size" value={s.tabSize} min={1} max={8} onChange={(v) => saveSettings({ tabSize: v })} />
          </Row>
          {toggle("formatOnSave", "Format on save", "Format the document each time you save it with Ctrl+S.")}
          {toggle("autoSave", "Auto save", "Save a file one second after you stop typing.")}
          {toggle("languageServers", "Language servers", "Start a language server found on PATH when you open a matching file. Servers that run project build scripts (Rust, C#, Java) always wait for you to start them from the status bar.")}
        </div>
        <h2 className="sub-title">Run</h2>
        <div className="panel">
          <Row label="Python interpreter" help="Used by Run and Debug. Point it at a virtual environment's python.exe, or leave empty to use the Python on PATH.">
            <input className="field" aria-label="Python interpreter" placeholder="python" defaultValue={s.python}
              onBlur={(e) => e.target.value !== s.python && saveSettings({ python: e.target.value.trim() })} />
          </Row>
        </div>
        <h2 className="sub-title">Terminal</h2>
        <div className="panel">
          <Row label="Shell" help="Program for new terminals, for example cmd.exe, pwsh.exe or bash.exe. Empty uses Windows PowerShell.">
            <input className="field" aria-label="Shell" placeholder="powershell.exe" defaultValue={s.shell}
              onBlur={(e) => e.target.value !== s.shell && saveSettings({ shell: e.target.value.trim() })} />
          </Row>
        </div>
        <h2 className="sub-title">Local AI</h2>
        <div className="panel">
          {toggle("aiEnabled", "Local AI assistant", "Chat about your code with a model running on this computer through Ollama. Nothing is sent anywhere else; only addresses on this computer are accepted.")}
          <Row label="Server address" help="Where the local model server listens.">
            <input className="field" aria-label="AI server address" defaultValue={s.aiUrl}
              onBlur={(e) => e.target.value !== s.aiUrl && saveSettings({ aiUrl: e.target.value.trim() })} />
          </Row>
        </div>
        <h2 className="sub-title">More</h2>
        <div className="panel">
          <Row label="settings.json" help='Custom run commands ("runners"), language server commands ("serverCommands") and everything above, as text. Use "Reload Settings from Disk" after editing.'>
            <button className="btn btn-sm" onClick={() => run("file.settingsJson")}>Open file</button>
          </Row>
          <Row label="Keyboard shortcuts" help="Change the key for any command.">
            <button className="btn btn-sm" onClick={() => run("file.shortcuts")}>Open</button>
          </Row>
        </div>
      </div>
    </div>
  );
}

export function KeysPage() {
  const keys = useStore((s) => s.keys);
  const [filter, setFilter] = useState("");
  const [recording, setRecording] = useState<string | null>(null);

  useEffect(() => {
    if (!recording) return;
    const onKey = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === "Escape") return setRecording(null);
      const chord = e.key === "Backspace" || e.key === "Delete" ? "" : chordOf(e);
      if (!chord && e.key !== "Backspace" && e.key !== "Delete") return;
      saveKeys({ ...keys, [recording]: chord });
      setRecording(null);
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [recording, keys]);

  const list = allCommands().filter((c) => `${c.category} ${c.title} ${keyFor(c)}`.toLowerCase().includes(filter.toLowerCase()));
  const used = new Map<string, number>();
  allCommands().forEach((c) => keyFor(c) && used.set(keyFor(c), (used.get(keyFor(c)) ?? 0) + 1));
  return (
    <div className="page-scroll">
      <div className="page-narrow">
        <h1 className="page-title">Keyboard shortcuts</h1>
        <p className="page-lede">Click a shortcut, then press the new keys. Backspace removes it; Esc cancels.</p>
        <input className="field field-wide" placeholder="Filter commands" value={filter} onChange={(e) => setFilter(e.target.value)} />
        <div className="panel keys-table">
          {list.map((c) => {
            const key = keyFor(c);
            const changed = c.id in keys;
            return (
              <div key={c.id} className="keys-row">
                <span><span className="keys-cat">{c.category}</span> {c.title}</span>
                {(used.get(key) ?? 0) > 1 && <span className="tag tag-danger">conflict</span>}
                {changed && (
                  <button className="btn-icon" title="Reset to default" onClick={() => {
                    const next = { ...keys };
                    delete next[c.id];
                    saveKeys(next);
                  }}>
                    <RotateCcw size={13} strokeWidth={2.5} />
                  </button>
                )}
                <button className={`kbd-btn${recording === c.id ? " is-recording" : ""}`} data-recording={recording === c.id || undefined}
                  onClick={() => setRecording(c.id)}>
                  {recording === c.id ? "Press keys…" : key || "—"}
                </button>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
