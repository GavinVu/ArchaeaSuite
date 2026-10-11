// Git integration: everything goes through the `git` program on PATH.
import { useSyncExternalStore } from "react";
import { api, join } from "./api";
import { get, log, onChange } from "./store";

export interface Change {
  /** Path relative to the repository root, forward slashes. */
  rel: string;
  path: string;
  /** Status in the index and in the working tree, as in `git status --short`. */
  index: string;
  tree: string;
}
export interface GitState {
  available: boolean;
  repo: boolean;
  top: string;
  branch: string;
  upstream: boolean;
  ahead: number;
  behind: number;
  changes: Change[];
}

const empty: GitState = { available: true, repo: false, top: "", branch: "", upstream: false, ahead: 0, behind: 0, changes: [] };
let state = empty;
const listeners = new Set<() => void>();
export const gitState = () => state;
export const useGit = () => useSyncExternalStore((l) => (listeners.add(l), () => listeners.delete(l)), () => state);

/** Runs git in the workspace. Throws git's own message when it fails. */
export async function git(args: string[], opts: { quiet?: boolean; timeout?: number } = {}): Promise<string> {
  const cwd = state.top || get().root;
  const out = await api.exec("git", ["-c", "core.quotepath=off", ...args], cwd, undefined, opts.timeout ?? 60);
  if (!opts.quiet) log(`> git ${args.join(" ")}\n${out.stdout}${out.stderr}`);
  if (out.code !== 0) throw new Error((out.stderr || out.stdout).trim() || `git exited with code ${out.code}`);
  return out.stdout;
}

let refreshing: Promise<void> | null = null;
export function refreshGit(): Promise<void> {
  return (refreshing ??= (async () => {
    let next = empty;
    const root = get().root;
    try {
      if (root && (await api.which("git"))) {
        state = { ...state, top: "" };
        const top = (await git(["rev-parse", "--show-toplevel"], { quiet: true })).trim().replace(/\//g, "\\");
        state = { ...state, top };
        const lines = (await git(["status", "--porcelain=v1", "-b", "--untracked-files=all"], { quiet: true })).split("\n");
        const head = lines.shift() ?? "";
        const m = /^## (?:No commits yet on )?(.+?)(?:\.\.\.(\S+))?(?: \[(.*)\])?$/.exec(head);
        next = {
          available: true,
          repo: true,
          top,
          branch: m?.[1] ?? "HEAD",
          upstream: !!m?.[2],
          ahead: Number(/ahead (\d+)/.exec(m?.[3] ?? "")?.[1] ?? 0),
          behind: Number(/behind (\d+)/.exec(m?.[3] ?? "")?.[1] ?? 0),
          changes: lines.filter(Boolean).map((line) => {
            const rel = line.slice(3).split(" -> ").pop()!.replace(/^"|"$/g, "");
            return { rel, path: join(top, rel.replace(/\//g, "\\")), index: line[0], tree: line[1] };
          }),
        };
      } else if (root) {
        next = { ...empty, available: false };
      }
    } catch {
      next = empty; // not a repository
    }
    state = next;
    refreshing = null;
    listeners.forEach((l) => l());
  })());
}

/** The committed version of a file ("" when it is new). */
export const headText = (rel: string) => git(["show", `HEAD:${rel}`], { quiet: true }).catch(() => "");

export async function branches(): Promise<string[]> {
  return (await git(["branch", "--format=%(refname:short)"], { quiet: true })).split("\n").filter(Boolean);
}

export interface Commit { hash: string; subject: string; author: string; when: string }
export async function recentCommits(): Promise<Commit[]> {
  const out = await git(["log", "-25", "--pretty=format:%h%x09%s%x09%an%x09%cr"], { quiet: true }).catch(() => "");
  return out.split("\n").filter(Boolean).map((l) => {
    const [hash, subject, author, when] = l.split("\t");
    return { hash, subject, author, when };
  });
}

// Refresh whenever files may have changed.
let lastRoot = get().root;
let lastFs = get().fsVersion;
onChange(() => {
  const s = get();
  if (s.root === lastRoot && s.fsVersion === lastFs) return;
  lastRoot = s.root;
  lastFs = s.fsVersion;
  void refreshGit();
});
