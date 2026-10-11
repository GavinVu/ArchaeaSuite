// Promise-based prompts: the quick-pick palette, confirm dialogs and context
// menus. Components in Overlays.tsx render whatever is pending here.
import { useSyncExternalStore } from "react";

export interface PickItem<T> { label: string; detail?: string; hint?: string; value: T }
export interface PickRequest {
  placeholder: string;
  /** Fixed list to filter, or nothing for free text input. */
  items?: PickItem<unknown>[];
  initial?: string;
  resolve: (value: unknown) => void;
}
export interface AskButton { label: string; value: string; kind?: "primary" | "danger" }
export interface AskRequest { title: string; body: string; buttons: AskButton[]; resolve: (v: string | null) => void }
export interface MenuItem { label: string; hint?: string; run?: () => void; separator?: boolean; disabled?: boolean }
export interface MenuRequest { x: number; y: number; items: MenuItem[] }

interface Overlay { pick: PickRequest | null; ask: AskRequest | null; menu: MenuRequest | null }
let overlay: Overlay = { pick: null, ask: null, menu: null };
const listeners = new Set<() => void>();
function update(patch: Partial<Overlay>) {
  overlay = { ...overlay, ...patch };
  listeners.forEach((l) => l());
}
export const useOverlay = () =>
  useSyncExternalStore((l) => (listeners.add(l), () => listeners.delete(l)), () => overlay);

/** Lets the user choose one item; resolves undefined when dismissed. */
export function pick<T>(items: PickItem<T>[], placeholder: string): Promise<T | undefined> {
  overlay.pick?.resolve(undefined);
  return new Promise((resolve) =>
    update({ pick: { placeholder, items, resolve: resolve as (v: unknown) => void } })
  );
}

/** Asks for a line of text; resolves undefined when dismissed. */
export function prompt(placeholder: string, initial = ""): Promise<string | undefined> {
  overlay.pick?.resolve(undefined);
  return new Promise((resolve) =>
    update({ pick: { placeholder, initial, resolve: resolve as (v: unknown) => void } })
  );
}

export function closePick(value: unknown) {
  const request = overlay.pick;
  update({ pick: null });
  request?.resolve(value);
}

/** A modal question; resolves null when dismissed. */
export function ask(title: string, body: string, buttons: AskButton[]): Promise<string | null> {
  return new Promise((resolve) => update({ ask: { title, body, buttons, resolve } }));
}

export function closeAsk(value: string | null) {
  const request = overlay.ask;
  update({ ask: null });
  request?.resolve(value);
}

export const openMenu = (x: number, y: number, items: MenuItem[]) => update({ menu: { x, y, items } });
export const closeMenu = () => overlay.menu && update({ menu: null });
