// The command registry and keyboard shortcuts. Menus, the command palette and
// key presses all run commands from here; extensions add to it at run time.
import { get } from "./store";

export interface Command { id: string; title: string; category: string; key?: string; run: () => unknown }

const commands = new Map<string, Command>();
export const register = (list: Command[]) => list.forEach((c) => commands.set(c.id, c));
export const unregister = (prefix: string) =>
  [...commands.keys()].filter((id) => id.startsWith(prefix)).forEach((id) => commands.delete(id));
export const allCommands = () => [...commands.values()];
export const run = (id: string) => void commands.get(id)?.run();

/** The shortcut in effect for a command: the user's override, else the default. "" means unbound. */
export const keyFor = (c: Command) => get().keys[c.id] ?? c.key ?? "";
export const keyOf = (id: string) => {
  const c = commands.get(id);
  return c ? keyFor(c) : "";
};

/** "Ctrl+Shift+P" for a keyboard event, or "" for a bare modifier. */
export function chordOf(e: KeyboardEvent): string {
  if (["Control", "Shift", "Alt", "Meta"].includes(e.key)) return "";
  const named: Record<string, string> = { " ": "Space", "`": "`", ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right", Escape: "Esc" };
  let key = named[e.key] ?? (e.key.length === 1 ? e.key.toUpperCase() : e.key);
  // With Ctrl held, e.key can be a control character or a shifted symbol; the physical key is steadier.
  if (e.code.startsWith("Key")) key = e.code.slice(3);
  else if (e.code.startsWith("Digit")) key = e.code.slice(5);
  else if (e.code === "Backquote") key = "`";
  else if (e.code === "Backslash") key = "\\";
  else if (e.code === "Comma") key = ",";
  else if (e.code === "Period") key = ".";
  else if (e.code === "Slash") key = "/";
  return [e.ctrlKey && "Ctrl", e.shiftKey && "Shift", e.altKey && "Alt", key].filter(Boolean).join("+");
}

/** Global key handling, in the capture phase so it runs before the editor's own. */
export function installKeys() {
  window.addEventListener(
    "keydown",
    (e) => {
      const chord = chordOf(e);
      // Plain typing never triggers a command.
      if (!chord || (!e.ctrlKey && !e.altKey && !/^(F\d+|Shift\+F\d+)$/.test(chord))) return;
      if ((e.target as HTMLElement | null)?.closest?.("[data-recording]")) return;
      const command = allCommands().find((c) => keyFor(c) === chord);
      if (!command) return;
      e.preventDefault();
      e.stopPropagation();
      void command.run();
    },
    true
  );
}
