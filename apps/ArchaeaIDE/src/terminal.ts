// Terminals (xterm.js over the backend's pseudo-terminals), the Run button and tasks.
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { api, basename, dirname, errorText, extname, join, on } from "./api";
import { activeFile, docFor, isDirty, saveFile } from "./editor";
import { pick } from "./overlay";
import { get, onChange, set, toast, type TermInfo } from "./store";

interface Term {
  info: TermInfo;
  xterm: Terminal;
  fit: FitAddon;
  /** Id of the live pseudo-terminal behind this view, if any. */
  pty: string | null;
  onExit?: (code: number) => void;
  onData?: (text: string) => void;
}
const terms = new Map<string, Term>();
const byPty = new Map<string, Term>();
let counter = 0;

const palette = (dark: boolean) =>
  dark
    ? { background: "#161616", foreground: "#f5f5f0", cursor: "#d6ff00", cursorAccent: "#161616",
        selectionBackground: "#d6ff0055", brightBlack: "#85857e", green: "#d6ff00", brightGreen: "#e4ff5c" }
    : { background: "#ffffff", foreground: "#161616", cursor: "#161616", cursorAccent: "#ffffff",
        selectionBackground: "#d6ff00", black: "#161616", brightBlack: "#72726a", white: "#4d4d47",
        brightWhite: "#161616", green: "#3f6212", brightGreen: "#3f6212", yellow: "#854d0e", brightYellow: "#854d0e",
        cyan: "#155e75", brightCyan: "#155e75", blue: "#1e40af", brightBlue: "#1e40af" };

const publish = () =>
  set({ terminals: [...terms.values()].map((t) => ({ ...t.info })) });

function create(kind: TermInfo["kind"], title: string, id = `t${++counter}`): Term {
  const s = get().settings;
  const xterm = new Terminal({
    fontFamily: '"IBM Plex Mono", Consolas, monospace',
    fontSize: Math.max(11, s.fontSize - 1),
    cursorBlink: true,
    scrollback: 8000,
    theme: palette(s.theme === "dark"),
  });
  const fit = new FitAddon();
  xterm.loadAddon(fit);
  const term: Term = { info: { id, title, kind, alive: false }, xterm, fit, pty: null };
  xterm.onData((data) => term.pty && void api.ptyWrite(term.pty, data).catch(() => {}));
  xterm.onResize(({ cols, rows }) => term.pty && void api.ptyResize(term.pty, cols, rows));
  // Copy with Ctrl+C when text is selected, paste with Ctrl+V, as in Windows Terminal.
  xterm.attachCustomKeyEventHandler((e) => {
    if (e.type !== "keydown" || !e.ctrlKey || e.altKey) return true;
    if (e.key.toLowerCase() === "c" && xterm.hasSelection()) {
      void navigator.clipboard.writeText(xterm.getSelection());
      xterm.clearSelection();
      return false;
    }
    if (e.key.toLowerCase() === "v") {
      void navigator.clipboard.readText().then((text) => {
        if (term.pty) void api.ptyWrite(term.pty, text);
      });
      e.preventDefault();
      return false;
    }
    return true;
  });
  terms.set(id, term);
  return term;
}

on<{ id: string; data: string }>("pty:data", ({ id, data }) => {
  const term = byPty.get(id);
  if (!term) return;
  const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
  term.xterm.write(bytes);
  term.onData?.(new TextDecoder().decode(bytes));
});
on<{ id: string; code: number }>("pty:exit", ({ id, code }) => {
  const term = byPty.get(id);
  byPty.delete(id);
  if (!term || term.pty !== id) return;
  term.pty = null;
  term.info.alive = false;
  const done = term.onExit;
  term.onExit = undefined;
  if (done) done(code);
  else term.xterm.write(`\r\n\x1b[90m[process exited with code ${code}]\x1b[0m\r\n`);
  publish();
});

let ptyCounter = 0;
async function start(term: Term, program?: string, args?: string[], cwd?: string | null) {
  const id = `${term.info.id}-${++ptyCounter}`;
  byPty.set(id, term);
  term.pty = id;
  term.info.alive = true;
  publish();
  try {
    await api.ptySpawn(id, { program, args, cwd, cols: term.xterm.cols, rows: term.xterm.rows });
  } catch (e) {
    byPty.delete(id);
    term.pty = null;
    term.info.alive = false;
    publish();
    throw e;
  }
}

function show(term: Term) {
  set({ panelOpen: true, panel: "terminal", activeTerminal: term.info.id });
  publish();
}

/** Mounts a terminal's screen into the panel. Returns the cleanup. */
export function mountTerminal(id: string, host: HTMLElement): () => void {
  const term = terms.get(id);
  if (!term) return () => {};
  if (term.xterm.element) host.appendChild(term.xterm.element);
  else term.xterm.open(host);
  const fit = () => {
    if (host.clientWidth > 40 && host.clientHeight > 20) term.fit.fit();
  };
  fit();
  const observer = new ResizeObserver(fit);
  observer.observe(host);
  term.xterm.focus();
  return () => {
    observer.disconnect();
    term.xterm.element?.remove();
  };
}

export async function newTerminal(cwd?: string | null) {
  const shell = get().settings.shell.trim();
  const n = [...terms.values()].filter((t) => t.info.kind === "shell").length + 1;
  const term = create("shell", shell ? basename(shell).replace(/\.exe$/i, "") : `powershell ${n}`);
  show(term);
  try {
    const [program, ...args] = shell ? splitCommand(shell) : [];
    await start(term, program, args, cwd ?? get().root ?? get().info?.home);
  } catch (e) {
    term.xterm.write(`\x1b[31mCould not start the shell: ${errorText(e)}\x1b[0m\r\n`);
  }
}

export function closeTerminal(id: string) {
  const term = terms.get(id);
  if (!term) return;
  if (term.pty) {
    void api.ptyKill(term.pty);
    byPty.delete(term.pty);
  }
  if (id === "run") set({ running: false });
  term.xterm.dispose();
  terms.delete(id);
  const rest = [...terms.keys()];
  set({ activeTerminal: get().activeTerminal === id ? (rest[rest.length - 1] ?? null) : get().activeTerminal });
  publish();
}

export const focusTerminal = () => {
  const id = get().activeTerminal;
  if (id) terms.get(id)?.xterm.focus();
};

/** Sends text to the active shell terminal, opening one if needed. */
export async function sendToTerminal(text: string) {
  let term = terms.get(get().activeTerminal ?? "");
  if (!term || term.info.kind !== "shell" || !term.pty) {
    term = [...terms.values()].find((t) => t.info.kind === "shell" && t.pty);
    if (!term) {
      await newTerminal();
      term = terms.get(get().activeTerminal ?? "");
      await new Promise((r) => setTimeout(r, 900)); // let the shell print its prompt
    }
  }
  if (!term?.pty) return;
  show(term);
  await api.ptyWrite(term.pty, text);
}

// Themes and font size follow the settings.
let lastSettings = get().settings;
onChange(() => {
  const s = get().settings;
  if (s === lastSettings) return;
  lastSettings = s;
  for (const t of terms.values()) {
    t.xterm.options.theme = palette(s.theme === "dark");
    t.xterm.options.fontSize = Math.max(11, s.fontSize - 1);
    if (t.xterm.element?.isConnected) t.fit.fit();
  }
});

// ---------------------------------------------------------------- running

/** Splits a command line into words, honouring double quotes. */
export function splitCommand(line: string): string[] {
  return [...line.matchAll(/"([^"]*)"|(\S+)/g)].map((m) => m[1] ?? m[2]);
}

export interface Step { program: string; args: string[]; cwd?: string }

/** Runs steps one after another in a terminal; a failing step stops the chain. */
async function runSteps(term: Term, steps: Step[], cwd: string | null, done: (code: number) => void) {
  const started = performance.now();
  const next = async (i: number): Promise<void> => {
    const step = steps[i];
    // Shown the way you would type it from the working folder.
    const here = (step.cwd ?? cwd ?? "").toLowerCase() + "\\";
    const shown = [basename(step.program).replace(/\.(exe|cmd|bat)$/i, ""), ...step.args]
      .map((a) => (here.length > 1 && a.toLowerCase().startsWith(here) ? a.slice(here.length) : a))
      .map((a) => (/\s/.test(a) ? `"${a}"` : a))
      .join(" ");
    term.xterm.write(`\x1b[90m> ${shown}\x1b[0m\r\n`);
    term.onExit = (code) => {
      if (code === 0 && i + 1 < steps.length) return void next(i + 1);
      const secs = ((performance.now() - started) / 1000).toFixed(1);
      term.xterm.write(`\r\n\x1b[90m[finished with exit code ${code} in ${secs}s]\x1b[0m\r\n`);
      done(code);
    };
    try {
      await start(term, step.program, step.args, step.cwd ?? cwd);
    } catch (e) {
      term.onExit = undefined;
      term.xterm.write(`\x1b[31m${errorText(e)}\x1b[0m\r\n`);
      done(1);
    }
  };
  await next(0);
}

const first = async (...programs: string[]) => {
  for (const p of programs) {
    const found = await api.which(p);
    if (found) return found;
  }
  return null;
};

async function findUp(dir: string, test: (name: string) => boolean): Promise<string | null> {
  const stop = get().root ? dirname(get().root!) : null;
  for (let d = dir, i = 0; d && i < 12 && d !== stop; d = dirname(d), i++) {
    const hit = (await api.listDir(d).catch(() => [])).find((e) => !e.dir && test(e.name));
    if (hit) return hit.path;
    if (dirname(d) === d) break;
  }
  return null;
}

const shellStep = (line: string, cwd?: string): Step => ({
  program: "powershell.exe",
  args: ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", line],
  cwd,
});

/** The Python to use: the one chosen in settings, else the first on PATH. */
export async function pythonPath(): Promise<string> {
  const chosen = get().settings.python.trim();
  const found = chosen ? await api.which(chosen) : await first("python", "py", "python3");
  if (!found) throw new Error(chosen ? `The Python interpreter in settings was not found: ${chosen}` : "Python was not found on PATH. Install it, then restart ArchaeaIDE.");
  return found;
}

/** Works out how to run a file. Throws a message for the user when it cannot. */
export async function stepsFor(file: string): Promise<Step[]> {
  const ext = extname(file);
  const dir = dirname(file);
  const need = async (what: string, ...programs: string[]) => {
    const found = await first(...programs);
    if (!found) throw new Error(`${what} was not found on PATH. Install it, then restart ArchaeaIDE.`);
    return found;
  };
  const custom = get().settings.runners[ext];
  if (custom) return [shellStep(custom.replace(/\$\{file\}/g, `'${file.replace(/'/g, "''")}'`), dir)];
  const tmp = `${get().info?.home ?? dir}\\AppData\\Local\\Temp\\archaea-ide-run`;
  const exe = join(tmp, basename(file).replace(/\.[^.]+$/, "") + ".exe");
  switch (ext) {
    case "py":
      return [{ program: await pythonPath(), args: ["-u", file] }];
    case "js": case "mjs": case "cjs": case "ts": case "mts":
      return [{ program: await need("Node.js", "node"), args: [file] }];
    case "c": {
      const cc = await need("A C compiler (gcc or clang)", "gcc", "clang");
      await api.createDir(tmp).catch(() => {});
      return [{ program: cc, args: [file, "-o", exe] }, { program: exe, args: [] }];
    }
    case "cpp": case "cc": case "cxx": {
      const cc = await need("A C++ compiler (g++ or clang++)", "g++", "clang++");
      await api.createDir(tmp).catch(() => {});
      return [{ program: cc, args: [file, "-std=c++17", "-o", exe] }, { program: exe, args: [] }];
    }
    case "cs": {
      const dotnet = await need("The .NET SDK", "dotnet");
      const project = await findUp(dir, (n) => n.toLowerCase().endsWith(".csproj"));
      if (project) return [{ program: dotnet, args: ["run", "--project", project] }];
      throw new Error("This C# file is not part of a project. Create one with File → New Project → C# console app.");
    }
    case "java":
      return [{ program: await need("Java (JDK 11 or newer)", "java"), args: [file] }];
    case "go":
      return [{ program: await need("Go", "go"), args: ["run", file] }];
    case "rs": {
      const manifest = await findUp(dir, (n) => n === "Cargo.toml");
      if (manifest) return [{ program: await need("Cargo", "cargo"), args: ["run", "--manifest-path", manifest] }];
      await api.createDir(tmp).catch(() => {});
      return [{ program: await need("The Rust compiler", "rustc"), args: [file, "-o", exe] }, { program: exe, args: [] }];
    }
    case "rb": return [{ program: await need("Ruby", "ruby"), args: [file] }];
    case "php": return [{ program: await need("PHP", "php"), args: [file] }];
    case "lua": return [{ program: await need("Lua", "lua", "luajit"), args: [file] }];
    case "pl": return [{ program: await need("Perl", "perl"), args: [file] }];
    case "r": return [{ program: await need("R", "Rscript"), args: [file] }];
    case "dart": return [{ program: await need("Dart", "dart"), args: ["run", file] }];
    case "sh": return [{ program: await need("Bash", "bash"), args: [file] }];
    case "ps1":
      return [{ program: "powershell.exe", args: ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file] }];
    case "bat": case "cmd":
      return [{ program: "cmd.exe", args: ["/d", "/c", file] }];
    default:
      throw new Error(
        `No runner is set up for .${ext || "this"} files. Add one under "runners" in settings.json, for example "${ext || "ext"}": "mytool \${file}".`
      );
  }
}

/** The single Run terminal, reused and cleared for every run. */
export function runTerminal(title: string): Term {
  let term = terms.get("run");
  if (term?.pty) {
    void api.ptyKill(term.pty);
    byPty.delete(term.pty);
    term.pty = null;
    term.onExit = undefined;
  }
  if (!term) term = create("run", title, "run");
  term.info.title = title;
  term.xterm.reset();
  show(term);
  return term;
}

/** Run button: saves the active file and runs it. Only ever called from a user action. */
export async function runActiveFile(extraArgs: string[] = [], hooks?: Pick<Term, "onData">): Promise<boolean> {
  const file = activeFile();
  if (!file) {
    toast("info", "Open a file to run it.");
    return false;
  }
  if (extname(file) === "html" || extname(file) === "htm") {
    const { openPath } = await import("@tauri-apps/plugin-opener");
    if (isDirty(file)) await saveFile(file);
    await openPath(file).catch((e) => toast("error", "Could not open the page", errorText(e)));
    return false;
  }
  if (docFor(file) && isDirty(file) && !(await saveFile(file))) return false;
  let steps: Step[];
  try {
    steps = await stepsFor(file);
  } catch (e) {
    toast("error", `Cannot run ${basename(file)}`, errorText(e));
    return false;
  }
  if (extraArgs.length) steps[steps.length - 1].args.unshift(...extraArgs);
  const term = runTerminal(`Run: ${basename(file)}`);
  term.onData = hooks?.onData;
  set({ running: true });
  // Give the panel a frame to lay out so the program starts at the right size.
  await new Promise((r) => setTimeout(r, 60));
  await runSteps(term, steps, dirname(file), () => {
    term.onData = undefined;
    set({ running: false });
  });
  return true;
}

export function stopRun() {
  const term = terms.get("run");
  if (!term?.pty) return;
  term.onExit = () => {};
  term.onData = undefined;
  void api.ptyKill(term.pty);
  term.xterm.write("\r\n\x1b[90m[stopped]\x1b[0m\r\n");
  set({ running: false });
}

// ------------------------------------------------------------------ tasks

export interface Task { label: string; command: string; cwd?: string; source: string }

/** Tasks from .archaea/tasks.json plus ones detected from project files. */
export async function findTasks(): Promise<Task[]> {
  const root = get().root;
  if (!root) return [];
  const tasks: Task[] = [];
  const read = (name: string) => api.readText(join(root, name)).then((f) => f.text).catch(() => null);
  try {
    const own = await read(".archaea\\tasks.json");
    for (const t of own ? (JSON.parse(own).tasks ?? []) : []) {
      if (typeof t.label === "string" && typeof t.command === "string")
        tasks.push({ label: t.label, command: t.command, cwd: t.cwd ? join(root, t.cwd) : root, source: "tasks.json" });
    }
  } catch (e) {
    toast("error", "tasks.json is not valid JSON", errorText(e));
  }
  try {
    const pkg = await read("package.json");
    for (const name of Object.keys(pkg ? (JSON.parse(pkg).scripts ?? {}) : {}))
      tasks.push({ label: `npm run ${name}`, command: `npm run ${name}`, cwd: root, source: "package.json" });
  } catch {
    /* an invalid package.json simply contributes no tasks */
  }
  const names = (await api.listDir(root).catch(() => [])).map((e) => e.name.toLowerCase());
  const add = (file: (n: string) => boolean, source: string, ...commands: string[]) => {
    if (names.some(file)) commands.forEach((c) => tasks.push({ label: c, command: c, cwd: root, source }));
  };
  add((n) => n === "cargo.toml", "Cargo.toml", "cargo build", "cargo run", "cargo test");
  add((n) => n.endsWith(".csproj") || n.endsWith(".sln"), ".NET", "dotnet build", "dotnet run", "dotnet test");
  add((n) => n === "go.mod", "go.mod", "go build ./...", "go test ./...");
  add((n) => n === "makefile", "Makefile", "make");
  add((n) => n === "requirements.txt", "requirements.txt", "python -m pip install -r requirements.txt");
  return tasks;
}

export async function runTask(task: Task) {
  const term = create("task", task.label);
  show(term);
  await new Promise((r) => setTimeout(r, 60));
  await runSteps(term, [shellStep(task.command, task.cwd)], task.cwd ?? get().root, () => {});
}

export async function pickTask() {
  if (!get().root) return toast("info", "Open a folder to run tasks.");
  const tasks = await findTasks();
  if (tasks.length === 0) return toast("info", "No tasks found.", "Use Terminal → Configure Tasks to add some.");
  const task = await pick(tasks.map((t) => ({ label: t.label, hint: t.source, value: t })), "Run which task?");
  if (task) await runTask(task);
}

export async function configureTasks(open: (path: string) => Promise<unknown>) {
  const root = get().root;
  if (!root) return toast("info", "Open a folder to configure tasks.");
  const path = join(root, ".archaea", "tasks.json");
  if (!(await api.stat(path)).exists) {
    await api.writeText(
      path,
      JSON.stringify({ tasks: [{ label: "List files", command: "Get-ChildItem" }] }, null, 2) + "\n"
    );
  }
  await open(path);
}
