import { useEffect, useRef } from "react";
import { Columns2, X } from "lucide-react";
import {
  attachEditor, closeTab, diffOriginals, docFor, editorOptions, monaco, pathOf, rememberView, selectTab, showInEditor,
  splitEditor,
} from "../editor";
import { refreshDecorations, toggleBreakpoint } from "../debug";
import { openMenu } from "../overlay";
import { get, set, useStore, type Group, type Tab } from "../store";
import { KeysPage, SettingsPage, Welcome } from "./Pages";
import { revealItemInDir } from "@tauri-apps/plugin-opener";

function DiffView({ tab }: { tab: Tab }) {
  const host = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const original = diffOriginals.get(tab.id);
    const doc = tab.path ? docFor(tab.path) : undefined;
    if (!host.current || !original || !doc) return;
    const editor = monaco.editor.createDiffEditor(host.current, {
      ...editorOptions(),
      automaticLayout: true,
      originalEditable: false,
      renderSideBySide: true,
    });
    editor.setModel({ original, modified: doc.model });
    return () => editor.dispose();
  }, [tab.id, tab.path]);
  return <div className="editor-host" ref={host} />;
}

function GroupView({ group }: { group: Group }) {
  const host = useRef<HTMLDivElement>(null);
  const dirty = useStore((s) => s.dirty);
  const isActive = useStore((s) => s.activeGroup === group.id);
  const many = useStore((s) => s.groups.length > 1);
  const tab = group.tabs.find((t) => t.id === group.active) ?? null;

  useEffect(() => {
    const editor = monaco.editor.create(host.current!, {
      ...editorOptions(),
      model: null,
      automaticLayout: true,
      glyphMargin: true,
      smoothScrolling: true,
      scrollBeyondLastLine: false,
      renderWhitespace: "selection",
      bracketPairColorization: { enabled: true },
      guides: { indentation: true, bracketPairs: true },
      padding: { top: 8 },
      fixedOverflowWidgets: true,
    });
    attachEditor(group.id, editor);
    const report = () => {
      if (get().activeGroup !== group.id) return;
      const model = editor.getModel();
      const pos = editor.getPosition();
      const selection = editor.getSelection();
      set({
        language: model?.getLanguageId() ?? "",
        cursor: pos && model
          ? { line: pos.lineNumber, col: pos.column, selected: selection ? model.getValueInRange(selection).length : 0 }
          : null,
      });
    };
    editor.onDidChangeCursorSelection(report);
    editor.onDidChangeModel(() => {
      report();
      refreshDecorations();
    });
    editor.onDidFocusEditorWidget(() => {
      set({ activeGroup: group.id });
      report();
    });
    editor.onMouseDown((e) => {
      const path = pathOf(editor.getModel());
      if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && path && e.target.position)
        toggleBreakpoint(path, e.target.position.lineNumber);
    });
    return () => {
      attachEditor(group.id, null);
      editor.dispose();
    };
  }, [group.id]);

  useEffect(() => {
    if (tab?.kind === "file") showInEditor(group.id, tab);
    if (isActive && tab?.kind !== "file") set({ cursor: null, language: "" });
  }, [group.id, tab, isActive]);

  const tabMenu = (e: React.MouseEvent, t: Tab) => {
    e.preventDefault();
    openMenu(e.clientX, e.clientY, [
      { label: "Close", run: () => void closeTab(group.id, t.id) },
      { label: "Close Others", run: async () => { for (const o of group.tabs) if (o.id !== t.id) await closeTab(group.id, o.id); } },
      { label: "Close All", run: async () => { for (const o of [...group.tabs]) await closeTab(group.id, o.id); } },
      { separator: true, label: "" },
      { label: "Copy Path", disabled: !t.path, run: () => void navigator.clipboard.writeText(t.path!) },
      { label: "Reveal in File Explorer", disabled: !t.path, run: () => void revealItemInDir(t.path!) },
    ]);
  };

  return (
    <section className={`group${isActive && many ? " is-active" : ""}`} onMouseDown={() => !isActive && set({ activeGroup: group.id })}>
      <div className="tabs" role="tablist">
        {group.tabs.map((t) => {
          const modified = t.kind === "file" && !!dirty[t.id];
          return (
            <div
              key={t.id}
              role="tab"
              aria-selected={t.id === group.active}
              className={`tab${t.id === group.active ? " is-active" : ""}`}
              title={t.path ?? t.title}
              onMouseDown={(e) => {
                if (e.button === 1) return void closeTab(group.id, t.id);
                if (e.button === 0) {
                  if (group.active) rememberView(group.id, group.active);
                  selectTab(group.id, t.id);
                }
              }}
              onContextMenu={(e) => tabMenu(e, t)}
            >
              <span className="tab-name">{t.title}</span>
              <button
                className={`tab-close${modified ? " is-dirty" : ""}`}
                aria-label={`Close ${t.title}`}
                onMouseDown={(e) => e.stopPropagation()}
                onClick={() => void closeTab(group.id, t.id)}
              >
                <X size={13} strokeWidth={2.5} />
              </button>
            </div>
          );
        })}
        <div className="tabs-fill" />
        {tab && (
          <button className="tabs-action" title="Split editor right" onClick={splitEditor}>
            <Columns2 size={15} strokeWidth={2.25} />
          </button>
        )}
      </div>
      <div className="group-body">
        <div className="editor-host" ref={host} hidden={tab?.kind !== "file"} />
        {tab?.kind === "diff" && <DiffView key={tab.id} tab={tab} />}
        {tab?.kind === "settings" && <SettingsPage />}
        {tab?.kind === "keys" && <KeysPage />}
        {(tab?.kind === "welcome" || !tab) && <Welcome />}
      </div>
    </section>
  );
}

export function EditorArea() {
  const groups = useStore((s) => s.groups);
  return (
    <div className="groups">
      {groups.map((g) => (
        <GroupView key={g.id} group={g} />
      ))}
    </div>
  );
}
