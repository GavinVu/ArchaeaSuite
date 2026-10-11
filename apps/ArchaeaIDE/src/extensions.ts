// Extensions: folders in <data>/extensions/<id>/ with an extension.json and an
// optional main.js. Each enabled extension's script runs in its own Web
// Worker, so it has no access to files, processes or the backend; it can only
// do what the message API below offers. Extensions are off until switched on.
import { useSyncExternalStore } from "react";
import { api, errorText, join } from "./api";
import { register, unregister } from "./commands";
import { activeEditor, monaco, onDidSave } from "./editor";
import { get, log, saveSettings, toast } from "./store";

export interface Extension {
  id: string;
  dir: string;
  name: string;
  version: string;
  description: string;
  main?: string;
  /** Language id -> snippets, in the VS Code snippet format. */
  snippets?: Record<string, Record<string, { prefix: string; body: string | string[]; description?: string }>>;
  error?: string;
}

let installed: Extension[] = [];
const listeners = new Set<() => void>();
export const useExtensions = () =>
  useSyncExternalStore((l) => (listeners.add(l), () => listeners.delete(l)), () => installed);
export const extensionsDir = () => join(get().info?.dataDir ?? "", "extensions");

interface Running { worker?: Worker; disposables: monaco.IDisposable[] }
const running = new Map<string, Running>();

// What an extension's main.js sees as `archaea`. It runs inside the worker.
const workerPrelude = `
const handlers = new Map(); const pending = new Map(); let seq = 0; const saveHandlers = [];
const call = (method, ...args) => new Promise((resolve) => { const id = ++seq; pending.set(id, resolve); postMessage({ call: method, id, args }); });
self.archaea = {
  commands: { register(id, title, fn) { handlers.set(id, fn); postMessage({ command: { id, title } }); } },
  window: { showMessage: (text) => call("showMessage", String(text)) },
  editor: {
    getText: () => call("getText"), getSelection: () => call("getSelection"), getPath: () => call("getPath"),
    replaceSelection: (text) => call("replaceSelection", String(text)), insert: (text) => call("replaceSelection", String(text)),
  },
  workspace: { onDidSave(fn) { saveHandlers.push(fn); } },
};
onmessage = async (e) => {
  const m = e.data;
  if (m.result !== undefined) { pending.get(m.id)?.(m.value); pending.delete(m.id); }
  else if (m.run) { try { await handlers.get(m.run)?.(); } catch (err) { postMessage({ error: String(err && err.stack || err) }); } }
  else if (m.saved) { for (const fn of saveHandlers) { try { await fn(m.saved); } catch (err) { postMessage({ error: String(err) }); } } }
};
`;

const hostApi: Record<string, (...args: string[]) => unknown> = {
  showMessage: (text) => toast("info", text),
  getText: () => activeEditor()?.getModel()?.getValue() ?? "",
  getPath: () => activeEditor()?.getModel()?.uri.fsPath ?? "",
  getSelection: () => {
    const editor = activeEditor();
    const selection = editor?.getSelection();
    return selection ? (editor!.getModel()?.getValueInRange(selection) ?? "") : "";
  },
  replaceSelection: (text) => {
    const editor = activeEditor();
    const selection = editor?.getSelection();
    if (editor && selection) editor.executeEdits("extension", [{ range: selection, text, forceMoveMarkers: true }]);
    return !!selection;
  },
};

async function activate(ext: Extension) {
  const entry: Running = { disposables: [] };
  running.set(ext.id, entry);
  for (const [language, snippets] of Object.entries(ext.snippets ?? {})) {
    entry.disposables.push(
      monaco.languages.registerCompletionItemProvider(language, {
        provideCompletionItems(model, pos) {
          const word = model.getWordUntilPosition(pos);
          const range = new monaco.Range(pos.lineNumber, word.startColumn, pos.lineNumber, word.endColumn);
          return {
            suggestions: Object.entries(snippets).map(([name, s]) => ({
              label: s.prefix,
              detail: `${name} (${ext.name})`,
              documentation: s.description,
              kind: monaco.languages.CompletionItemKind.Snippet,
              insertText: Array.isArray(s.body) ? s.body.join("\n") : s.body,
              insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
              range,
            })),
          };
        },
      })
    );
  }
  if (!ext.main) return;
  const source = (await api.readText(join(ext.dir, ext.main))).text;
  const url = URL.createObjectURL(new Blob([workerPrelude, "\n", source], { type: "text/javascript" }));
  const worker = new Worker(url);
  entry.worker = worker;
  worker.onmessage = async (e) => {
    const m = e.data;
    if (m.command) {
      const id = `ext.${ext.id}.${m.command.id}`;
      register([{ id, title: String(m.command.title), category: ext.name, run: () => worker.postMessage({ run: m.command.id }) }]);
    } else if (m.call) {
      const value = (await hostApi[m.call]?.(...(m.args ?? []))) ?? null;
      worker.postMessage({ result: true, id: m.id, value });
    } else if (m.error) {
      log(`[${ext.name}] ${m.error}\n`);
      toast("error", `${ext.name} reported an error`, m.error.split("\n")[0]);
    }
  };
  worker.onerror = (e) => {
    log(`[${ext.name}] ${e.message}\n`);
    toast("error", `${ext.name} failed to load`, e.message);
  };
}

function deactivate(id: string) {
  const entry = running.get(id);
  entry?.worker?.terminate();
  entry?.disposables.forEach((d) => d.dispose());
  running.delete(id);
  unregister(`ext.${id}.`);
}

onDidSave((path) => running.forEach((r) => r.worker?.postMessage({ saved: path })));

/** Reads the extensions folder and (re)starts the enabled ones. */
export async function loadExtensions() {
  [...running.keys()].forEach(deactivate);
  const dirs = (await api.listDir(extensionsDir()).catch(() => [])).filter((e) => e.dir);
  installed = await Promise.all(
    dirs.map(async (d): Promise<Extension> => {
      const base = { id: d.name, dir: d.path, name: d.name, version: "", description: "" };
      try {
        const manifest = JSON.parse((await api.readText(join(d.path, "extension.json"))).text);
        return { ...base, ...manifest, id: d.name, dir: d.path };
      } catch (e) {
        return { ...base, error: `extension.json: ${errorText(e)}` };
      }
    })
  );
  for (const ext of installed) {
    if (!get().settings.extensions.includes(ext.id) || ext.error) continue;
    await activate(ext).catch((e) => toast("error", `Could not start ${ext.name}`, errorText(e)));
  }
  listeners.forEach((l) => l());
}

export async function setExtensionEnabled(id: string, enabled: boolean) {
  const list = get().settings.extensions.filter((e) => e !== id);
  saveSettings({ extensions: enabled ? [...list, id] : list });
  await loadExtensions();
}

/** Writes a small working extension to learn from. */
export async function createSampleExtension(): Promise<string> {
  const dir = join(extensionsDir(), "sample-word-count");
  await api.writeText(
    join(dir, "extension.json"),
    JSON.stringify(
      {
        name: "Sample: Word Count",
        version: "1.0.0",
        description: "Adds a Count Words command, an Uppercase Selection command and a 'todo' snippet.",
        main: "main.js",
        snippets: { javascript: { "TODO comment": { prefix: "todo", body: "// TODO(${1:name}): ${2:what}", description: "A TODO comment" } } },
      },
      null,
      2
    ) + "\n"
  );
  await api.writeText(
    join(dir, "main.js"),
    `// Runs in a sandboxed worker. The global \`archaea\` object is the whole API:
//   archaea.commands.register(id, title, fn)
//   archaea.window.showMessage(text)
//   archaea.editor.getText() / getSelection() / getPath() / replaceSelection(text)
//   archaea.workspace.onDidSave(fn)

archaea.commands.register("countWords", "Count Words in File", async () => {
  const text = await archaea.editor.getText();
  const words = text.split(/\\s+/).filter(Boolean).length;
  await archaea.window.showMessage(\`\${words} words, \${text.length} characters\`);
});

archaea.commands.register("upper", "Uppercase Selection", async () => {
  const selected = await archaea.editor.getSelection();
  if (selected) await archaea.editor.replaceSelection(selected.toUpperCase());
});
`
  );
  await loadExtensions();
  return dir;
}
