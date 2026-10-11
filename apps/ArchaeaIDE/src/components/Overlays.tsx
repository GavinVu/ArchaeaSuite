import { useEffect, useMemo, useRef, useState } from "react";
import { X } from "lucide-react";
import { closeAsk, closeMenu, closePick, useOverlay, type PickItem, type PickRequest } from "../overlay";
import { dismissToast, useStore } from "../store";

/** Subsequence match; lower is better, -1 is no match. */
function score(text: string, query: string): number {
  if (!query) return 0;
  const t = text.toLowerCase();
  const at = t.indexOf(query);
  if (at >= 0) return at;
  let i = 0, gaps = 0, last = -1;
  for (const ch of query) {
    i = t.indexOf(ch, i);
    if (i < 0) return -1;
    if (last >= 0) gaps += i - last - 1;
    last = i++;
  }
  return 1000 + gaps;
}

function Pick({ request }: { request: PickRequest }) {
  const [text, setText] = useState(request.initial ?? "");
  const [index, setIndex] = useState(0);
  const list = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    input.current?.focus();
    input.current?.select();
  }, []);
  const items = useMemo(() => {
    if (!request.items) return [];
    const q = text.trim().toLowerCase();
    return request.items
      .map((item) => ({ item, s: Math.min(...[item.label, item.detail ?? ""].map((t) => score(t, q)).filter((s) => s >= 0), Infinity) }))
      .filter((x) => x.s !== Infinity)
      .sort((a, b) => a.s - b.s)
      .slice(0, 200)
      .map((x) => x.item);
  }, [request.items, text]);
  useEffect(() => {
    setIndex(0);
  }, [text]);
  useEffect(() => {
    list.current?.children[index]?.scrollIntoView({ block: "nearest" });
  }, [index]);

  const accept = (item?: PickItem<unknown>) => {
    if (request.items) return item && closePick(item.value);
    closePick(text);
  };
  return (
    <div className="pick-wrap" onMouseDown={() => closePick(undefined)}>
      <div className="pick" role="dialog" aria-label={request.placeholder} onMouseDown={(e) => e.stopPropagation()}>
        <input
          ref={input}
          className="field"
          value={text}
          placeholder={request.placeholder}
          spellCheck={false}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Escape") closePick(undefined);
            else if (e.key === "Enter") accept(items[index]);
            else if (e.key === "ArrowDown") (e.preventDefault(), setIndex((i) => Math.min(items.length - 1, i + 1)));
            else if (e.key === "ArrowUp") (e.preventDefault(), setIndex((i) => Math.max(0, i - 1)));
          }}
        />
        {request.items ? (
          <div className="pick-list" ref={list}>
            {items.map((item, i) => (
              <div key={i} className={`pick-item${i === index ? " is-active" : ""}`} onMouseEnter={() => setIndex(i)} onClick={() => accept(item)}>
                <span className="pick-label">{item.label}</span>
                {item.detail && <span className="pick-detail">{item.detail}</span>}
                {item.hint && <kbd>{item.hint}</kbd>}
              </div>
            ))}
            {items.length === 0 && <div className="pick-none">No matches</div>}
          </div>
        ) : (
          <div className="pick-none">{request.placeholder} — Enter to confirm, Esc to cancel</div>
        )}
      </div>
    </div>
  );
}

export function Overlays() {
  const { pick, ask, menu } = useOverlay();
  const toasts = useStore((s) => s.toasts);
  useEffect(() => {
    if (!menu) return;
    const close = () => closeMenu();
    window.addEventListener("mousedown", close);
    window.addEventListener("blur", close);
    window.addEventListener("keydown", close);
    return () => {
      window.removeEventListener("mousedown", close);
      window.removeEventListener("blur", close);
      window.removeEventListener("keydown", close);
    };
  }, [menu]);
  useEffect(() => {
    if (!ask) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && closeAsk(null);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [ask]);

  return (
    <>
      {menu && (
        <div
          className="menu"
          role="menu"
          style={{ left: Math.min(menu.x, window.innerWidth - 250), top: Math.min(menu.y, window.innerHeight - menu.items.length * 30 - 16) }}
          onMouseDown={(e) => e.stopPropagation()}
        >
          {menu.items.map((item, i) =>
            item.separator ? <div key={i} className="menu-sep" /> : (
              <button key={i} role="menuitem" disabled={item.disabled} onClick={() => (closeMenu(), item.run?.())}>
                <span>{item.label}</span>
                {item.hint && <kbd>{item.hint}</kbd>}
              </button>
            )
          )}
        </div>
      )}
      {pick && <Pick key={pick.placeholder + (pick.items?.length ?? "")} request={pick} />}
      {ask && (
        <div className="dialog-wrap" onClick={() => closeAsk(null)}>
          <div className="dialog" role="alertdialog" aria-modal="true" aria-labelledby="ask-title" aria-describedby="ask-body" onClick={(e) => e.stopPropagation()}>
            <h2 id="ask-title">{ask.title}</h2>
            <p id="ask-body">{ask.body}</p>
            <div className="dialog-actions">
              {[...ask.buttons].reverse().map((b, i) => (
                <button key={b.value} autoFocus={i === ask.buttons.length - 1}
                  className={`btn ${b.kind === "primary" ? "btn-primary" : b.kind === "danger" ? "btn-danger" : ""}`}
                  onClick={() => closeAsk(b.value)}>
                  {b.label}
                </button>
              ))}
            </div>
          </div>
        </div>
      )}
      <div className="toasts" role="status" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`toast is-${t.tone === "ok" ? "success" : t.tone}`}>
            <div className="toast-body">
              <div className="toast-title">{t.title}</div>
              {t.detail && <div className="toast-detail selectable">{t.detail}</div>}
            </div>
            <button className="toast-close" onClick={() => dismissToast(t.id)} aria-label="Dismiss"><X size={15} strokeWidth={2.5} /></button>
          </div>
        ))}
      </div>
    </>
  );
}
