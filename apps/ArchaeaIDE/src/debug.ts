// Debugging. Python uses debugpy over the Debug Adapter Protocol; JavaScript
// and TypeScript use Node.js's built-in inspector (Chrome DevTools Protocol).
// Both are driven through the same small Backend interface.
import { useSyncExternalStore } from "react";
import { api, basename, dirname, errorText, extname, on, samePath } from "./api";
import { activeFile, allEditors, isDirty, monaco, openFile, pathOf, saveFile } from "./editor";
import { ask } from "./overlay";
import { get, onChange, saveSession, set, toast } from "./store";
import { pythonPath, runActiveFile, runTask, stopRun } from "./terminal";

type Json = any; // eslint-disable-line @typescript-eslint/no-explicit-any
export interface Frame { id: string; name: string; path: string | null; line: number }
export interface Variable { name: string; value: string; ref: string | null }
export interface Scope { name: string; ref: string }
export interface DebugState {
  status: "idle" | "starting" | "running" | "paused";
  reason: string;
  frames: Frame[];
  frame: string | null;
  scopes: Scope[];
  log: string;
  /** Bumped at every stop so variable views reload. */
  stops: number;
}

interface Backend {
  resume(): void;
  next(): void;
  stepIn(): void;
  stepOut(): void;
  pause(): void;
  stop(): void;
  scopes(frame: Frame): Promise<Scope[]>;
  variables(ref: string): Promise<Variable[]>;
  evaluate(expression: string, frame: string | null): Promise<string>;
  setBreakpoints(path: string, lines: number[]): void;
}

const idle: DebugState = { status: "idle", reason: "", frames: [], frame: null, scopes: [], log: "", stops: 0 };
let state = idle;
let backend: Backend | null = null;
const listeners = new Set<() => void>();
const update = (patch: Partial<DebugState>) => {
  state = { ...state, ...patch };
  listeners.forEach((l) => l());
  refreshDecorations();
};
export const useDebug = () => useSyncExternalStore((l) => (listeners.add(l), () => listeners.delete(l)), () => state);
export const debugState = () => state;
const print = (text: string) => update({ log: (state.log + text).slice(-100_000) });

function ended() {
  backend = null;
  update({ status: "idle", frames: [], frame: null, scopes: [], reason: "" });
}

async function stopped(frames: Frame[], reason: string) {
  const top = frames.find((f) => f.path) ?? frames[0];
  update({ status: "paused", frames, reason, frame: top?.id ?? null, scopes: [], stops: state.stops + 1 });
  if (top) await selectFrame(top);
}

export async function selectFrame(frame: Frame) {
  update({ frame: frame.id, scopes: (await backend?.scopes(frame).catch(() => [])) ?? [] });
  if (frame.path) await openFile(frame.path, { line: frame.line });
  refreshDecorations();
}

export const debug = {
  resume: () => (backend?.resume(), update({ status: "running", frames: [], frame: null, scopes: [] })),
  next: () => backend?.next(),
  stepIn: () => backend?.stepIn(),
  stepOut: () => backend?.stepOut(),
  pause: () => backend?.pause(),
  stop: () => backend?.stop(),
  variables: (ref: string) => backend?.variables(ref) ?? Promise.resolve([]),
  async evaluate(expression: string) {
    print(`> ${expression}\n`);
    if (!backend || state.status !== "paused") return print("Pause the program at a breakpoint to evaluate expressions.\n");
    print((await backend.evaluate(expression, state.frame).catch((e) => `Error: ${errorText(e)}`)) + "\n");
  },
};

// ------------------------------------------------------------ breakpoints

export function toggleBreakpoint(path: string, line: number) {
  const k = path.toLowerCase();
  const lines = get().breakpoints[k] ?? [];
  const next = lines.includes(line) ? lines.filter((l) => l !== line) : [...lines, line].sort((a, b) => a - b);
  const all = { ...get().breakpoints, [k]: next };
  if (next.length === 0) delete all[k];
  set({ breakpoints: all });
  saveSession();
  backend?.setBreakpoints(path, next);
  refreshDecorations();
}

export function clearBreakpoints() {
  const old = get().breakpoints;
  set({ breakpoints: {} });
  saveSession();
  Object.keys(old).forEach((p) => backend?.setBreakpoints(p, []));
  refreshDecorations();
}

const collections = new WeakMap<object, monaco.editor.IEditorDecorationsCollection>();
/** Redraws breakpoint dots and the paused-line marker in every editor. */
export function refreshDecorations() {
  const frame = state.frames.find((f) => f.id === state.frame);
  for (const editor of allEditors()) {
    const path = pathOf(editor.getModel());
    const decorations: monaco.editor.IModelDeltaDecoration[] = [];
    if (path) {
      for (const line of get().breakpoints[path.toLowerCase()] ?? [])
        decorations.push({ range: new monaco.Range(line, 1, line, 1), options: { glyphMarginClassName: "bp-glyph", stickiness: 1 } });
      if (frame?.path && samePath(frame.path, path))
        decorations.push({
          range: new monaco.Range(frame.line, 1, frame.line, 1),
          options: { isWholeLine: true, className: "debug-line", glyphMarginClassName: "debug-arrow" },
        });
    }
    let collection = collections.get(editor);
    if (!collection) collections.set(editor, (collection = editor.createDecorationsCollection()));
    collection.set(decorations);
  }
}

// ------------------------------------------------------------------ start

export async function startDebugging() {
  if (state.status !== "idle") return debug.resume();
  const file = activeFile();
  if (!file) return toast("info", "Open a file to debug it.");
  const ext = extname(file);
  if (isDirty(file) && !(await saveFile(file))) return;
  update({ ...idle, status: "starting", log: "" });
  set({ side: "debug", sideOpen: true });
  try {
    if (ext === "py") await startPython(file);
    else if (["js", "mjs", "cjs", "ts", "mts"].includes(ext)) await startNode();
    else {
      ended();
      toast("info", "No debugger for this file type", "Debugging works with Python (debugpy) and JavaScript/TypeScript (Node.js). Other languages can still be run.");
    }
  } catch (e) {
    backend?.stop();
    ended();
    toast("error", "Could not start debugging", errorText(e));
  }
}

// -------------------------------------------------- Python: debugpy (DAP)

let dapSeq = 0;
async function startPython(file: string) {
  const python = await pythonPath();
  if ((await api.exec(python, ["-c", "import debugpy"])).code !== 0) {
    ended();
    const answer = await ask("Install debugpy?", "Debugging Python needs the debugpy package. Install it for this Python with pip?", [
      { label: "Install", value: "yes", kind: "primary" },
      { label: "Cancel", value: "no" },
    ]);
    if (answer === "yes")
      await runTask({ label: "Install debugpy", command: `& '${python}' -m pip install debugpy`, source: "" });
    return;
  }
  const id = `dap-${++dapSeq}`;
  let seq = 0;
  let thread = 0;
  const pending = new Map<number, { resolve: (v: Json) => void; reject: (e: Error) => void }>();
  const request = (command: string, args: Json = {}) =>
    new Promise<Json>((resolve, reject) => {
      const n = ++seq;
      pending.set(n, { resolve, reject });
      api.rpcSend(id, JSON.stringify({ seq: n, type: "request", command, arguments: args })).catch(reject);
    });
  const sendBreakpoints = (path: string, lines: number[]) =>
    request("setBreakpoints", { source: { path }, breakpoints: lines.map((line) => ({ line })) }).catch(() => {});

  sessions.set(id, {
    async message(m: Json) {
      if (m.type === "response") {
        const waiting = pending.get(m.request_seq);
        pending.delete(m.request_seq);
        if (m.success) waiting?.resolve(m.body ?? {});
        else waiting?.reject(new Error(m.message ?? `${m.command} failed`));
      } else if (m.type === "event") {
        const body = m.body ?? {};
        if (m.event === "initialized") {
          for (const [path, lines] of Object.entries(get().breakpoints)) await sendBreakpoints(path, lines);
          await request("configurationDone").catch(() => {});
          update({ status: "running" });
        } else if (m.event === "stopped") {
          thread = body.threadId ?? thread;
          const trace = await request("stackTrace", { threadId: thread, startFrame: 0, levels: 60 }).catch(() => ({ stackFrames: [] }));
          const frames: Frame[] = trace.stackFrames.map((f: Json) => ({ id: String(f.id), name: f.name, path: f.source?.path ?? null, line: f.line }));
          await stopped(frames, body.description ?? body.reason ?? "paused");
        } else if (m.event === "continued") {
          update({ status: "running", frames: [], frame: null, scopes: [] });
        } else if (m.event === "output" && body.category !== "telemetry") {
          print(body.output ?? "");
        } else if (m.event === "terminated") {
          void api.rpcStop(id);
        }
      }
    },
    exit() {
      pending.forEach((p) => p.reject(new Error("debugger stopped")));
      if (backend === self) {
        print("[debugging ended]\n");
        ended();
      }
    },
  });
  const step = (command: string) => void request(command, { threadId: thread }).catch(() => {});
  const self: Backend = {
    resume: () => step("continue"),
    next: () => step("next"),
    stepIn: () => step("stepIn"),
    stepOut: () => step("stepOut"),
    pause: () => step("pause"),
    stop() {
      void request("disconnect", { terminateDebuggee: true }).catch(() => {});
      setTimeout(() => void api.rpcStop(id), 600);
    },
    scopes: async (frame) =>
      (await request("scopes", { frameId: Number(frame.id) })).scopes.map((s: Json) => ({ name: s.name, ref: String(s.variablesReference) })),
    variables: async (ref) =>
      (await request("variables", { variablesReference: Number(ref) })).variables.map((v: Json) => ({
        name: v.name,
        value: v.value,
        ref: v.variablesReference ? String(v.variablesReference) : null,
      })),
    evaluate: async (expression, frame) =>
      (await request("evaluate", { expression, frameId: frame ? Number(frame) : undefined, context: "repl" })).result,
    setBreakpoints: (path, lines) => void sendBreakpoints(path, lines),
  };
  backend = self;
  set({ panelOpen: true, panel: "console" });
  await api.rpcStart(id, python, ["-m", "debugpy.adapter"], dirname(file));
  await request("initialize", { clientID: "archaea-ide", adapterID: "python", linesStartAt1: true, columnsStartAt1: true, pathFormat: "path" });
  print(`Debugging ${basename(file)} with debugpy\n`);
  // The launch response only arrives after configuration is done, so it is not awaited.
  request("launch", { type: "python", request: "launch", program: file, cwd: dirname(file), console: "internalConsole", justMyCode: true }).catch(
    (e) => toast("error", "The debugger could not launch the program", errorText(e))
  );
}

const sessions = new Map<string, { message(m: Json): void; exit(): void }>();
on<{ id: string; text: string }>("rpc:msg", ({ id, text }) => {
  try {
    sessions.get(id)?.message(JSON.parse(text));
  } catch {
    /* ignore malformed adapter output */
  }
});
on<{ id: string }>("rpc:exit", ({ id }) => {
  sessions.get(id)?.exit();
  sessions.delete(id);
});

// --------------------------------------------- Node.js: inspector (CDP)

const fileUrlToPath = (url: string) =>
  url.startsWith("file:///") ? decodeURIComponent(url.slice(8)).replace(/\//g, "\\") : null;

async function startNode() {
  let seen = "";
  let connected = false;
  const started = await runActiveFile(["--inspect-brk=127.0.0.1:0"], {
    onData(text) {
      if (connected) return;
      // The address may be split by terminal escape codes or line wrapping.
      seen = (seen + text).replace(/\x1b\[[0-9;?]*[A-Za-z]|[\r\n]/g, "").slice(-4000);
      const m = /ws:\/\/127\.0\.0\.1:\d+\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/.exec(seen);
      if (m) {
        connected = true;
        attachNode(m[0]);
      }
    },
  });
  if (!started) return ended();
  // If the program ends before the debugger attaches (a syntax error, say), give up.
  const off = onChange(() => {
    if (get().running) return;
    off();
    if (!connected && state.status === "starting") ended();
  });
}

function attachNode(url: string) {
  const ws = new WebSocket(url);
  let seq = 0;
  let first = true;
  const pending = new Map<number, { resolve: (v: Json) => void; reject: (e: Error) => void }>();
  const send = (method: string, params: Json = {}) =>
    new Promise<Json>((resolve, reject) => {
      const id = ++seq;
      pending.set(id, { resolve, reject });
      ws.send(JSON.stringify({ id, method, params }));
    });
  const ids = new Map<string, string[]>();
  const frames = new Map<string, Json>();
  /** Script id -> URL; call frames do not always carry the URL themselves. */
  const scripts = new Map<string, string>();
  const setBreakpoints = async (path: string, lines: number[]) => {
    const k = path.toLowerCase();
    for (const id of ids.get(k) ?? []) await send("Debugger.removeBreakpoint", { breakpointId: id }).catch(() => {});
    // Windows paths are case-insensitive; match the file URL without regard to case.
    const urlRegex = encodeURI(path.replace(/\\/g, "/")).replace(/#/g, "%23").replace(/\?/g, "%3F").split("")
      .map((c) => (/[a-z]/i.test(c) ? `[${c.toLowerCase()}${c.toUpperCase()}]` : c.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")))
      .join("");
    const made: string[] = [];
    for (const line of lines) {
      const r = await send("Debugger.setBreakpointByUrl", { lineNumber: line - 1, urlRegex: `^file:///${urlRegex}$` }).catch(() => null);
      if (r?.breakpointId) made.push(r.breakpointId);
    }
    ids.set(k, made);
  };
  const describe = (v: Json): string =>
    v.type === "string" ? JSON.stringify(v.value) : v.type === "undefined" ? "undefined" : (v.description ?? String(v.value));

  const self: Backend = {
    resume: () => void send("Debugger.resume"),
    next: () => void send("Debugger.stepOver"),
    stepIn: () => void send("Debugger.stepInto"),
    stepOut: () => void send("Debugger.stepOut"),
    pause: () => void send("Debugger.pause"),
    stop() {
      ws.close();
      stopRun();
    },
    scopes: async (frame) =>
      (frames.get(frame.id)?.scopeChain ?? [])
        .filter((s: Json) => s.type !== "global" && s.object?.objectId)
        .map((s: Json) => ({ name: s.name ? `${s.type}: ${s.name}` : s.type, ref: s.object.objectId })),
    variables: async (ref) =>
      ((await send("Runtime.getProperties", { objectId: ref, ownProperties: true })).result ?? [])
        .filter((p: Json) => p.value)
        .map((p: Json) => ({ name: p.name, value: describe(p.value), ref: p.value.objectId ?? null })),
    async evaluate(expression, frame) {
      const r = await send("Debugger.evaluateOnCallFrame", { callFrameId: frame, expression });
      return r.exceptionDetails ? `Error: ${r.exceptionDetails.exception?.description ?? r.exceptionDetails.text}` : describe(r.result);
    },
    setBreakpoints: (path, lines) => void setBreakpoints(path, lines),
  };
  backend = self;

  ws.onopen = async () => {
    await send("Runtime.enable");
    await send("Debugger.enable");
    for (const [path, lines] of Object.entries(get().breakpoints)) await setBreakpoints(path, lines);
    await send("Runtime.runIfWaitingForDebugger");
    update({ status: "running" });
  };
  ws.onmessage = (event) => {
    const m = JSON.parse(event.data as string);
    if (m.id) {
      const waiting = pending.get(m.id);
      pending.delete(m.id);
      if (m.error) waiting?.reject(new Error(m.error.message));
      else waiting?.resolve(m.result);
    } else if (m.method === "Debugger.scriptParsed") {
      scripts.set(m.params.scriptId, m.params.url);
    } else if (m.method === "Debugger.paused") {
      // --inspect-brk stops before the first line; breakpoints are set by now, so go on.
      if (first) {
        first = false;
        return void send("Debugger.resume");
      }
      frames.clear();
      const list: Frame[] = m.params.callFrames.map((f: Json) => {
        frames.set(f.callFrameId, f);
        return { id: f.callFrameId, name: f.functionName || "(anonymous)", path: fileUrlToPath(f.url || scripts.get(f.location.scriptId) || ""), line: f.location.lineNumber + 1 };
      });
      void stopped(list, m.params.reason === "other" ? "breakpoint" : m.params.reason);
    } else if (m.method === "Debugger.resumed") {
      if (state.status === "paused") update({ status: "running", frames: [], frame: null, scopes: [] });
    } else if (m.method === "Runtime.executionContextDestroyed") {
      ws.close(); // lets Node exit instead of waiting for the debugger
    }
  };
  ws.onclose = () => {
    pending.forEach((p) => p.reject(new Error("debugger disconnected")));
    if (backend === self) ended();
  };
  ws.onerror = () => toast("error", "Could not connect to the Node.js debugger");
}
