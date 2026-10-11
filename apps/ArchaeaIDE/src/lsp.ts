// A small Language Server Protocol client. JavaScript, TypeScript, JSON, CSS
// and HTML are handled by Monaco's built-in services; every other language
// gets completion, hover, diagnostics, navigation, rename and formatting from
// a language server found on PATH.
import { useSyncExternalStore } from "react";
import { api, errorText, on } from "./api";
import { monaco, onDidOpen, onDidSave, docFor, openDocs } from "./editor";
import { splitCommand } from "./terminal";
import { get, log, toast } from "./store";

interface ServerSpec { commands: string[]; /** Runs project build scripts, so it is never started automatically. */ builds?: boolean }

export const servers: Record<string, ServerSpec> = {
  python: { commands: ["pyright-langserver --stdio", "basedpyright-langserver --stdio", "pylsp", "jedi-language-server"] },
  c: { commands: ["clangd"] },
  cpp: { commands: ["clangd"] },
  rust: { commands: ["rust-analyzer"], builds: true },
  go: { commands: ["gopls"] },
  csharp: { commands: ["csharp-ls", "OmniSharp -lsp"], builds: true },
  java: { commands: ["jdtls"], builds: true },
  lua: { commands: ["lua-language-server"] },
  ruby: { commands: ["ruby-lsp", "solargraph stdio"] },
  php: { commands: ["intelephense --stdio"] },
  shell: { commands: ["bash-language-server start"] },
  yaml: { commands: ["yaml-language-server --stdio"] },
};

type Json = any; // eslint-disable-line @typescript-eslint/no-explicit-any
export type ServerStatus = "starting" | "running" | "stopped" | "missing" | "manual";

class Client {
  status: ServerStatus = "starting";
  name = "";
  caps: Json = {};
  private seq = 0;
  private pending = new Map<number, { resolve: (v: Json) => void; reject: (e: Error) => void }>();
  private versions = new Map<string, number>();
  private ready: Promise<void>;

  constructor(readonly id: string, readonly language: string, program: string, args: string[], root: string) {
    this.name = program;
    this.ready = (async () => {
      await api.rpcStart(id, program, args, root);
      const rootUri = monaco.Uri.file(root).toString();
      const result = await this.request("initialize", {
        processId: null,
        rootUri,
        workspaceFolders: [{ uri: rootUri, name: root }],
        capabilities: {
          textDocument: {
            synchronization: { didSave: true },
            completion: { completionItem: { snippetSupport: true, documentationFormat: ["markdown", "plaintext"] } },
            hover: { contentFormat: ["markdown", "plaintext"] },
            signatureHelp: { signatureInformation: { documentationFormat: ["markdown", "plaintext"] } },
            publishDiagnostics: {},
            definition: {}, references: {}, rename: {}, formatting: {}, documentSymbol: { hierarchicalDocumentSymbolSupport: true },
          },
          workspace: { workspaceFolders: true, configuration: true },
        },
      });
      this.caps = result?.capabilities ?? {};
      this.notify("initialized", {});
      this.status = "running";
      changed();
      for (const doc of openDocs()) if (doc.model.getLanguageId() === language) this.open(doc.model);
    })();
    this.ready.catch((e) => {
      this.status = "stopped";
      changed();
      toast("error", `Could not start the ${language} language server`, errorText(e));
    });
  }

  private send(message: Json) {
    return api.rpcSend(this.id, JSON.stringify({ jsonrpc: "2.0", ...message }));
  }
  request(method: string, params: Json): Promise<Json> {
    const id = ++this.seq;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.send({ id, method, params }).catch(reject);
    });
  }
  notify(method: string, params: Json) {
    void this.send({ method, params }).catch(() => {});
  }
  /** A request made on behalf of an editor feature: waits for start-up, never throws. */
  async ask(method: string, params: Json): Promise<Json> {
    try {
      await this.ready;
      return this.status === "running" ? await this.request(method, params) : null;
    } catch {
      return null;
    }
  }

  receive(message: Json) {
    if (message.id !== undefined && message.method === undefined) {
      const waiting = this.pending.get(message.id);
      this.pending.delete(message.id);
      if (message.error) waiting?.reject(new Error(message.error.message));
      else waiting?.resolve(message.result);
    } else if (message.id !== undefined) {
      // Requests from the server: answer the ones that need an answer.
      const result = message.method === "workspace/configuration" ? (message.params.items ?? []).map(() => null) : null;
      void this.send({ id: message.id, result });
    } else if (message.method === "textDocument/publishDiagnostics") {
      const model = monaco.editor.getModel(monaco.Uri.parse(message.params.uri));
      if (model) monaco.editor.setModelMarkers(model, `lsp-${this.language}`, message.params.diagnostics.map(marker));
    } else if (message.method === "window/showMessage" || message.method === "window/logMessage") {
      log(`[${this.language}] ${message.params.message}\n`);
    }
  }

  open(model: monaco.editor.ITextModel) {
    const uri = model.uri.toString();
    if (this.versions.has(uri) || this.status !== "running") return;
    this.versions.set(uri, 1);
    this.notify("textDocument/didOpen", { textDocument: { uri, languageId: this.language, version: 1, text: model.getValue() } });
    model.onDidChangeContent(() => {
      const version = (this.versions.get(uri) ?? 1) + 1;
      this.versions.set(uri, version);
      if (this.status === "running")
        this.notify("textDocument/didChange", { textDocument: { uri, version }, contentChanges: [{ text: model.getValue() }] });
    });
    model.onWillDispose(() => {
      this.versions.delete(uri);
      if (this.status === "running") this.notify("textDocument/didClose", { textDocument: { uri } });
    });
  }

  stopped() {
    this.status = "stopped";
    this.pending.forEach((p) => p.reject(new Error("language server stopped")));
    this.pending.clear();
    this.versions.clear();
    monaco.editor.getModels().forEach((m) => monaco.editor.setModelMarkers(m, `lsp-${this.language}`, []));
  }
}

const clients = new Map<string, Client>();
const statuses = new Map<string, ServerStatus>();
const listeners = new Set<() => void>();
let snapshot = 0;
function changed() {
  snapshot++;
  listeners.forEach((l) => l());
}
/** Status of the server for a language, for the status bar. */
export function useServerStatus(language: string): { status: ServerStatus | null; name: string } {
  useSyncExternalStore((l) => (listeners.add(l), () => listeners.delete(l)), () => snapshot);
  const client = clients.get(language);
  return { status: client?.status ?? statuses.get(language) ?? null, name: client?.name ?? "" };
}

on<{ id: string; text: string }>("rpc:msg", ({ id, text }) => {
  const client = [...clients.values()].find((c) => c.id === id);
  if (!client) return;
  try {
    client.receive(JSON.parse(text));
  } catch (e) {
    log(`[${client.language}] bad message: ${errorText(e)}\n`);
  }
});
on<{ id: string; text: string }>("rpc:log", ({ id, text }) => {
  if (id.startsWith("lsp-")) log(text);
});
on<{ id: string }>("rpc:exit", ({ id }) => {
  const client = [...clients.values()].find((c) => c.id === id);
  if (!client) return;
  client.stopped();
  clients.delete(client.language);
  statuses.set(client.language, "stopped");
  changed();
});

let generation = 0;
/** Starts the server for a language. `manual` is true when the user asked for it. */
export async function startServer(language: string, manual = false) {
  const root = get().root;
  const spec = servers[language];
  const custom = get().settings.serverCommands[language];
  if (!root || (!spec && !custom) || clients.has(language)) return;
  if (!manual && (!get().settings.languageServers || statuses.get(language) === "stopped")) return;
  if (!manual && spec?.builds && !custom) {
    statuses.set(language, "manual");
    return changed();
  }
  for (const line of custom ? [custom] : spec.commands) {
    const [program, ...args] = splitCommand(line);
    const found = await api.which(program);
    if (!found) continue;
    if (clients.has(language)) return;
    clients.set(language, new Client(`lsp-${language}-${++generation}`, language, found, args, root));
    registerProviders(language);
    return changed();
  }
  statuses.set(language, "missing");
  changed();
  if (manual)
    toast("info", `No ${language} language server found`, `Install one of: ${spec?.commands.map((c) => c.split(" ")[0]).join(", ")}`);
}

export function stopServer(language: string) {
  const client = clients.get(language);
  if (client) void api.rpcStop(client.id);
}

export function stopAllServers() {
  clients.forEach((c) => void api.rpcStop(c.id));
  statuses.clear();
}

onDidOpen((doc) => {
  const language = doc.model.getLanguageId();
  const client = clients.get(language);
  if (client) client.open(doc.model);
  else void startServer(language);
});
onDidSave((path) => {
  const doc = docFor(path);
  const client = doc && clients.get(doc.model.getLanguageId());
  if (client?.status === "running") client.notify("textDocument/didSave", { textDocument: { uri: doc!.model.uri.toString() } });
});

// ---------------------------------------------- protocol <-> Monaco shapes

const range = (r: Json) =>
  new monaco.Range(r.start.line + 1, r.start.character + 1, r.end.line + 1, r.end.character + 1);
const position = (p: monaco.Position) => ({ line: p.lineNumber - 1, character: p.column - 1 });
const docPos = (model: monaco.editor.ITextModel, p: monaco.Position) => ({
  textDocument: { uri: model.uri.toString() },
  position: position(p),
});
const marker = (d: Json): monaco.editor.IMarkerData => ({
  severity: [0, monaco.MarkerSeverity.Error, monaco.MarkerSeverity.Warning, monaco.MarkerSeverity.Info, monaco.MarkerSeverity.Hint][d.severity ?? 1],
  message: d.message,
  source: d.source,
  code: d.code === undefined ? undefined : String(d.code),
  startLineNumber: d.range.start.line + 1,
  startColumn: d.range.start.character + 1,
  endLineNumber: d.range.end.line + 1,
  endColumn: d.range.end.character + 1,
});
const markdown = (c: Json): monaco.IMarkdownString[] => {
  if (!c) return [];
  if (Array.isArray(c)) return c.flatMap(markdown);
  if (typeof c === "string") return [{ value: c }];
  if (c.language) return [{ value: "```" + c.language + "\n" + c.value + "\n```" }];
  return [{ value: c.kind === "plaintext" ? c.value.replace(/[\\`*_{}[\]()#+\-.!<>]/g, "\\$&") : c.value }];
};
const locations = (result: Json): monaco.languages.Location[] =>
  (Array.isArray(result) ? result : result ? [result] : []).map((l: Json) => ({
    uri: monaco.Uri.parse(l.uri ?? l.targetUri),
    range: range(l.range ?? l.targetSelectionRange),
  }));
const edits = (list: Json): monaco.languages.TextEdit[] =>
  (list ?? []).map((e: Json) => ({ range: range(e.range), text: e.newText }));

const registered = new Set<string>();
function registerProviders(language: string) {
  if (registered.has(language)) return;
  registered.add(language);
  const client = () => clients.get(language);
  const L = monaco.languages;

  L.registerCompletionItemProvider(language, {
    triggerCharacters: [".", ":", ">", '"', "'", "/", "@", "<", "#"],
    async provideCompletionItems(model, pos) {
      const result = await client()?.ask("textDocument/completion", docPos(model, pos));
      const items: Json[] = Array.isArray(result) ? result : (result?.items ?? []);
      const word = model.getWordUntilPosition(pos);
      const fallback = new monaco.Range(pos.lineNumber, word.startColumn, pos.lineNumber, word.endColumn);
      return {
        incomplete: !!result?.isIncomplete,
        suggestions: items.map((i) => ({
          label: i.label,
          // LSP kinds are 1-based and in a different order than Monaco's.
          kind: lspKind[i.kind] ?? L.CompletionItemKind.Text,
          detail: i.detail,
          documentation: i.documentation ? markdown(i.documentation)[0] : undefined,
          sortText: i.sortText,
          filterText: i.filterText,
          insertText: i.textEdit?.newText ?? i.insertText ?? i.label,
          insertTextRules: i.insertTextFormat === 2 ? L.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
          range: i.textEdit ? range(i.textEdit.range ?? i.textEdit.insert) : fallback,
          additionalTextEdits: i.additionalTextEdits ? edits(i.additionalTextEdits) : undefined,
        })),
      };
    },
  });
  L.registerHoverProvider(language, {
    async provideHover(model, pos) {
      const r = await client()?.ask("textDocument/hover", docPos(model, pos));
      return r ? { contents: markdown(r.contents), range: r.range ? range(r.range) : undefined } : null;
    },
  });
  L.registerSignatureHelpProvider(language, {
    signatureHelpTriggerCharacters: ["(", ","],
    async provideSignatureHelp(model, pos) {
      const r = await client()?.ask("textDocument/signatureHelp", docPos(model, pos));
      if (!r?.signatures?.length) return null;
      return {
        value: {
          activeSignature: r.activeSignature ?? 0,
          activeParameter: r.activeParameter ?? 0,
          signatures: r.signatures.map((s: Json) => ({
            label: s.label,
            documentation: s.documentation ? markdown(s.documentation)[0] : undefined,
            parameters: (s.parameters ?? []).map((p: Json) => ({ label: p.label, documentation: p.documentation ? markdown(p.documentation)[0] : undefined })),
          })),
        },
        dispose() {},
      };
    },
  });
  L.registerDefinitionProvider(language, {
    provideDefinition: async (model, pos) => locations(await client()?.ask("textDocument/definition", docPos(model, pos))),
  });
  L.registerReferenceProvider(language, {
    provideReferences: async (model, pos, context) =>
      locations(await client()?.ask("textDocument/references", { ...docPos(model, pos), context })),
  });
  L.registerRenameProvider(language, {
    async provideRenameEdits(model, pos, newName) {
      const r = await client()?.ask("textDocument/rename", { ...docPos(model, pos), newName });
      const changes: Record<string, Json[]> = { ...(r?.changes ?? {}) };
      for (const dc of r?.documentChanges ?? []) if (dc.textDocument) changes[dc.textDocument.uri] = dc.edits;
      return {
        edits: Object.entries(changes).flatMap(([uri, list]) =>
          list.map((e) => ({ resource: monaco.Uri.parse(uri), versionId: undefined, textEdit: { range: range(e.range), text: e.newText } }))
        ),
      };
    },
  });
  L.registerDocumentFormattingEditProvider(language, {
    async provideDocumentFormattingEdits(model, options) {
      return edits(await client()?.ask("textDocument/formatting", {
        textDocument: { uri: model.uri.toString() },
        options: { tabSize: options.tabSize, insertSpaces: options.insertSpaces },
      }));
    },
  });
  L.registerDocumentSymbolProvider(language, {
    async provideDocumentSymbols(model) {
      const r: Json[] = (await client()?.ask("textDocument/documentSymbol", { textDocument: { uri: model.uri.toString() } })) ?? [];
      const convert = (s: Json): monaco.languages.DocumentSymbol => ({
        name: s.name,
        detail: s.detail ?? "",
        kind: (s.kind ?? 13) - 1,
        tags: [],
        range: range(s.range ?? s.location.range),
        selectionRange: range(s.selectionRange ?? s.location.range),
        children: (s.children ?? []).map(convert),
      });
      return r.map(convert);
    },
  });
}

const K = monaco.languages.CompletionItemKind;
const lspKind: Record<number, monaco.languages.CompletionItemKind> = {
  1: K.Text, 2: K.Method, 3: K.Function, 4: K.Constructor, 5: K.Field, 6: K.Variable, 7: K.Class, 8: K.Interface,
  9: K.Module, 10: K.Property, 11: K.Unit, 12: K.Value, 13: K.Enum, 14: K.Keyword, 15: K.Snippet, 16: K.Color,
  17: K.File, 18: K.Reference, 19: K.Folder, 20: K.EnumMember, 21: K.Constant, 22: K.Struct, 23: K.Event,
  24: K.Operator, 25: K.TypeParameter,
};
