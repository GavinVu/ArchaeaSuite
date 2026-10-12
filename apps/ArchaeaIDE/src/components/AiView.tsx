// The local AI assistant: chat with a model served by Ollama on this computer,
// plus the guided setup (install Ollama, start it, download a model).
import { useEffect, useRef, useState } from "react";
import { Download, Plus, RefreshCw, Send, Square, Trash2 } from "lucide-react";
import { openUrl } from "@tauri-apps/plugin-opener";
import { api, basename, errorText, on } from "../api";
import { activeEditor } from "../editor";
import { get, saveSettings, toast, useStore } from "../store";
import { runTask } from "../terminal";

interface Message { role: "user" | "assistant" | "system"; content: string }
let chatSeq = 0;
const chatHandlers = new Map<string, (chunk: { text: string; done: boolean; error: string | null }) => void>();
on<{ id: string; text: string; done: boolean; error: string | null }>("ai:chunk", (c) => chatHandlers.get(c.id)?.(c));
let history: Message[] = [];

/** Models that run acceptably on an ordinary PC without a graphics card. */
const suggested = [
  { name: "qwen2.5-coder:1.5b", note: "1 GB · fast, for any PC" },
  { name: "qwen2.5-coder:7b", note: "4.7 GB · better answers, 16 GB RAM" },
  { name: "llama3.2:3b", note: "2 GB · general questions" },
];

/** Where ollama.exe is: on PATH, or where its installer puts it (PATH is stale until the IDE restarts). */
async function findOllama(): Promise<string | null> {
  const onPath = await api.which("ollama");
  if (onPath) return onPath;
  const installed = `${get().info?.home}\\AppData\\Local\\Programs\\Ollama\\ollama.exe`;
  return (await api.stat(installed)).exists ? installed : null;
}

type Setup = "checking" | "ready" | "not-installed" | "not-running" | "no-models";

/** Splits a reply into prose and fenced code blocks. */
function parts(content: string): { code: boolean; text: string }[] {
  return content.split(/```[^\n]*\n?/).map((text, i) => ({ code: i % 2 === 1, text })).filter((p) => p.text.trim());
}

function insertAtCursor(code: string) {
  const editor = activeEditor();
  const selection = editor?.getSelection();
  if (!editor || !selection) return toast("info", "Open a file to insert into.");
  editor.executeEdits("ai", [{ range: selection, text: code, forceMoveMarkers: true }]);
  editor.focus();
}

export function AiView() {
  const settings = useStore((s) => s.settings);
  const [setup, setSetup] = useState<Setup>("checking");
  const [models, setModels] = useState<string[]>([]);
  const [detail, setDetail] = useState("");
  const [adding, setAdding] = useState(false);
  const [custom, setCustom] = useState("");
  const [messages, setMessages] = useState<Message[]>(history);
  const [text, setText] = useState("");
  const [streaming, setStreaming] = useState<string | null>(null);
  const [withCode, setWithCode] = useState(true);
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => void (history = messages), [messages]);
  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" });
  }, [messages]);

  // A finished download closes the list of models to download.
  useEffect(() => setAdding(false), [models.length]);

  const check = async () => {
    try {
      const list = await api.aiModels(settings.aiUrl);
      setModels(list);
      setDetail("");
      if (list.length > 0 && !list.includes(get().settings.aiModel)) saveSettings({ aiModel: list[0] });
      setSetup(list.length ? "ready" : "no-models");
    } catch (e) {
      setDetail(errorText(e));
      setSetup((await findOllama()) ? "not-running" : "not-installed");
    }
  };
  // While something is missing, keep looking so the view moves on by itself
  // once an install or a download finishes.
  useEffect(() => {
    if (!settings.aiEnabled) return;
    void check();
    if (setup === "ready" && !adding) return;
    const timer = setInterval(() => void check(), 4000);
    return () => clearInterval(timer);
  }, [settings.aiEnabled, settings.aiUrl, setup, adding]); // eslint-disable-line react-hooks/exhaustive-deps

  const install = () =>
    runTask({
      label: "Install Ollama",
      command: "winget install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements",
      source: "",
    });
  const start = async () => {
    const program = await findOllama();
    if (!program) return setSetup("not-installed");
    await api.aiStart(program).catch((e) => toast("error", "Could not start Ollama", errorText(e)));
    setTimeout(() => void check(), 1500);
  };
  const download = async (name: string) => {
    const model = name.trim();
    if (!/^[\w.\-/]+(:[\w.\-]+)?$/.test(model)) return toast("error", "That does not look like a model name.", "Example: qwen2.5-coder:1.5b");
    const program = await findOllama();
    if (!program) return setSetup("not-installed");
    setAdding(true);
    await runTask({ label: `Download ${model}`, command: `& '${program}' pull ${model}`, source: "" });
  };

  if (!settings.aiEnabled)
    return (
      <>
        <div className="side-head"><span>Local AI</span></div>
        <div className="side-empty">
          <p>Ask questions about your code using a model that runs on this computer with <b>Ollama</b>. It is optional, free and off by default. Your code never leaves this PC.</p>
          <button className="btn btn-primary btn-wide" onClick={() => saveSettings({ aiEnabled: true })}>Turn on local AI</button>
        </div>
      </>
    );

  const send = async () => {
    const question = text.trim();
    if (!question || streaming) return;
    const model = activeEditor()?.getModel();
    const selection = activeEditor()?.getSelection();
    let context = "";
    if (withCode && model) {
      const selected = selection && !selection.isEmpty() ? model.getValueInRange(selection) : "";
      const code = (selected || model.getValue()).slice(0, 24_000);
      context = `\n\n${selected ? "Selected code" : "Current file"} (${basename(model.uri.fsPath)}):\n\`\`\`${model.getLanguageId()}\n${code}\n\`\`\``;
    }
    setMessages([...messages, { role: "user", content: question }, { role: "assistant", content: "" }]);
    setText("");
    const id = `chat-${++chatSeq}`;
    setStreaming(id);
    chatHandlers.set(id, (chunk) => {
      if (chunk.text) setMessages((m) => [...m.slice(0, -1), { role: "assistant", content: m[m.length - 1].content + chunk.text }]);
      if (chunk.error) setMessages((m) => [...m.slice(0, -1), { role: "assistant", content: `${m[m.length - 1].content}\n[${chunk.error}]`.trim() }]);
      if (chunk.done) {
        chatHandlers.delete(id);
        setStreaming(null);
      }
    });
    const sent: Message[] = [
      { role: "system", content: "You are a concise programming assistant inside a code editor. Answer briefly. Put code in fenced code blocks." },
      ...messages,
      { role: "user", content: question + context },
    ];
    await api.aiChat(id, settings.aiUrl, settings.aiModel, sent).catch((e) => {
      chatHandlers.get(id)?.({ text: "", done: true, error: errorText(e) });
    });
  };

  const modelChoices = (
    <>
      {suggested.filter((s) => !models.includes(s.name)).map((s) => (
        <button key={s.name} className="model-choice" onClick={() => void download(s.name)}>
          <Download size={14} strokeWidth={2.5} />
          <span>{s.name}</span>
          <span className="side-note">{s.note}</span>
        </button>
      ))}
      <div className="field-row">
        <input className="field" placeholder="Another model, e.g. codellama:7b" value={custom} aria-label="Model name"
          onChange={(e) => setCustom(e.target.value)} onKeyDown={(e) => e.key === "Enter" && void download(custom)} />
        <button className="btn-icon" title="Download this model" disabled={!custom.trim()} onClick={() => void download(custom)}><Download size={15} strokeWidth={2.25} /></button>
      </div>
      <p className="side-note">Downloads run in the terminal panel. The model appears here when it finishes.</p>
    </>
  );

  return (
    <>
      <div className="side-head">
        <span>Local AI</span>
        {setup === "ready" && <button className="btn-icon" title="Download another model" aria-pressed={adding} onClick={() => setAdding(!adding)}><Plus size={15} strokeWidth={2.25} /></button>}
        <button className="btn-icon" title="Reconnect" onClick={() => void check()}><RefreshCw size={14} strokeWidth={2.25} /></button>
        <button className="btn-icon" title="Clear conversation" onClick={() => setMessages([])}><Trash2 size={14} strokeWidth={2.25} /></button>
      </div>
      {setup === "checking" && <p className="side-note pad">Looking for Ollama…</p>}
      {setup === "not-installed" && (
        <div className="side-empty">
          <p><b>Step 1 of 2 — install Ollama.</b> Ollama is the free program that runs AI models on your PC. Installing it downloads about 1.5 GB.</p>
          <button className="btn btn-primary btn-wide" onClick={() => void install()}>Install Ollama</button>
          <button className="btn btn-wide" onClick={() => void openUrl("https://ollama.com/download")}>Open download page</button>
          <p className="side-note">The install runs in the terminal panel using winget. This view continues by itself when it is done.</p>
          <button className="btn btn-wide" onClick={() => saveSettings({ aiEnabled: false })}>Turn off local AI</button>
        </div>
      )}
      {setup === "not-running" && (
        <div className="side-empty">
          <p>Ollama is installed but not running.</p>
          <button className="btn btn-primary btn-wide" onClick={() => void start()}>Start Ollama</button>
          <p className="side-note">{detail}</p>
        </div>
      )}
      {setup === "no-models" && (
        <div className="side-empty">
          <p><b>Step 2 of 2 — download a model.</b> Pick one to start with; you can add more later.</p>
          {modelChoices}
        </div>
      )}
      {setup === "ready" && (
        <>
          <div className="side-form">
            <select className="field" aria-label="Model" value={settings.aiModel} onChange={(e) => saveSettings({ aiModel: e.target.value })}>
              {models.map((m) => <option key={m}>{m}</option>)}
            </select>
            {adding && modelChoices}
          </div>
          <div className="side-scroll chat">
            {messages.length === 0 && <p className="side-note pad">Ask about the open file, or select code first to ask about just that. The first answer takes longer while the model loads.</p>}
            {messages.map((m, i) => (
              <div key={i} className={`chat-msg is-${m.role}`}>
                {m.content === "" && streaming ? <div className="chat-text">Thinking…</div> : parts(m.content).map((p, n) =>
                  p.code ? (
                    <div key={n} className="chat-code">
                      <pre className="selectable">{p.text.replace(/\n$/, "")}</pre>
                      <button className="btn btn-sm" onClick={() => insertAtCursor(p.text)}>Insert at cursor</button>
                    </div>
                  ) : <div key={n} className="chat-text selectable">{p.text.trim()}</div>
                )}
              </div>
            ))}
            <div ref={end} />
          </div>
          <div className="side-form">
            <label className="check"><input type="checkbox" checked={withCode} onChange={(e) => setWithCode(e.target.checked)} /> Include selection or current file</label>
            <div className="field-row">
              <textarea className="field" rows={2} placeholder="Ask about your code" value={text} aria-label="Message"
                onChange={(e) => setText(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && !e.shiftKey && (e.preventDefault(), void send())} />
              {streaming
                ? <button className="btn-icon is-stop" title="Stop" onClick={() => void api.aiCancel(streaming)}><Square size={13} strokeWidth={3} /></button>
                : <button className="btn-icon" title="Send" onClick={() => void send()}><Send size={15} strokeWidth={2.25} /></button>}
            </div>
          </div>
        </>
      )}
    </>
  );
}
