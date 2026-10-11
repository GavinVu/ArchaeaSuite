import { useEffect, useRef } from "react";
import { ExternalLink, FolderOpen, Play, Trash2, Wrench, X } from "lucide-react";
import { openUrl } from "@tauri-apps/plugin-opener";
import { formatBytes, formatDate } from "../format";
import {
  installApp,
  launchApp,
  openAppFolder,
  requestUninstall,
  selectApp,
  useStore,
} from "../store";
import type { AppEntry } from "../types";
import { AppIcon } from "./AppIcon";
import { OpProgress, PrimaryAction, StateTag } from "./EntryActions";

/** Renders plain-text notes: blank lines separate paragraphs, "- " starts a bullet. */
export function Prose({ text }: { text: string }) {
  const blocks: JSX.Element[] = [];
  let bullets: string[] = [];
  let para: string[] = [];
  const flush = () => {
    if (bullets.length) {
      blocks.push(
        <ul key={blocks.length}>
          {bullets.map((b, i) => (
            <li key={i}>{b}</li>
          ))}
        </ul>
      );
      bullets = [];
    }
    if (para.length) {
      blocks.push(<p key={blocks.length}>{para.join(" ")}</p>);
      para = [];
    }
  };
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    const bullet = /^[-*•]\s+(.*)$/.exec(line);
    if (bullet) {
      if (para.length) flush();
      bullets.push(bullet[1]);
    } else if (!line) {
      flush();
    } else {
      if (bullets.length) flush();
      para.push(line);
    }
  }
  flush();
  return <div className="prose selectable">{blocks}</div>;
}

const ARCH_LABEL: Record<string, string> = { x64: "x64", arm64: "ARM64" };

export function AppDrawer({ entry }: { entry: AppEntry }) {
  const op = useStore((s) => s.ops[entry.id]);
  const systemArch = useStore((s) => s.system?.arch ?? "x64");
  const closeRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const { catalog, installed } = entry;

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        selectApp(null);
      } else if (e.key === "Tab" && panelRef.current) {
        // Keep keyboard focus inside the panel while it is open.
        const items = panelRef.current.querySelectorAll<HTMLElement>(
          "button:not(:disabled), a[href], [tabindex]:not([tabindex='-1'])"
        );
        if (!items.length) return;
        const first = items[0];
        const last = items[items.length - 1];
        if (e.shiftKey && document.activeElement === first) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
          e.preventDefault();
          first.focus();
        }
      }
    };
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("keydown", onKey);
      previous?.focus?.();
    };
  }, [entry.id]);

  const archs = catalog ? Object.keys(catalog.packages) : [];
  const facts: [string, string][] = [];
  if (installed) facts.push(["Installed", `v${installed.version}`]);
  if (catalog?.version) facts.push(["Latest", `v${catalog.version}`]);
  if (catalog?.releaseDate) facts.push(["Released", formatDate(catalog.releaseDate)]);
  if (entry.pkg) facts.push(["Download", formatBytes(entry.pkg.size)]);
  if (installed) facts.push(["On disk", formatBytes(installed.sizeBytes)]);
  if (archs.length)
    facts.push(["Builds", archs.map((a) => ARCH_LABEL[a] ?? a).join(", ")]);
  if (entry.pkgArch && entry.pkgArch !== systemArch && !installed)
    facts.push(["This PC", `${ARCH_LABEL[entry.pkgArch]} build, emulated`]);
  if (entry.category) facts.push(["Category", entry.category]);
  if (installed) facts.push(["Location", installed.installPath]);

  return (
    <>
      <div className="scrim" onClick={() => selectApp(null)} />
      <aside
        className="drawer"
        role="dialog"
        aria-modal="true"
        aria-labelledby="drawer-title"
        ref={panelRef}
      >
        <header className="drawer-head">
          <AppIcon name={entry.name} icon={entry.icon} size={72} />
          <div className="drawer-titles">
            <h2 className="drawer-name" id="drawer-title">
              {entry.name}
            </h2>
            <p className="drawer-tagline">{entry.tagline}</p>
            <div className="card-meta" style={{ marginTop: 10 }}>
              <StateTag entry={entry} />
            </div>
          </div>
          <button
            ref={closeRef}
            className="btn btn-ghost btn-icon"
            onClick={() => selectApp(null)}
            aria-label="Close details"
          >
            <X size={17} strokeWidth={2.5} />
          </button>
        </header>

        <div className="drawer-body">
          {entry.description && (
            <section>
              <h3 className="sub-title">About</h3>
              <Prose text={entry.description} />
            </section>
          )}
          {!catalog && (
            <section className="prose">
              <p>
                This app isn't in the catalog right now — you may be offline. It still launches
                normally; updates and repair return once the catalog is reachable.
              </p>
            </section>
          )}
          {facts.length > 0 && (
            <section>
              <h3 className="sub-title">Details</h3>
              <dl className="facts">
                {facts.map(([k, v]) => (
                  <div key={k} style={{ display: "contents" }}>
                    <dt>{k}</dt>
                    <dd className="selectable">{v}</dd>
                  </div>
                ))}
              </dl>
            </section>
          )}
          {catalog?.releaseNotes && (
            <section>
              <h3 className="sub-title">
                What's new{catalog.version ? ` in v${catalog.version}` : ""}
              </h3>
              <Prose text={catalog.releaseNotes} />
            </section>
          )}
        </div>

        <footer className="drawer-foot">
          {op ? (
            <div className="drawer-foot-row" style={{ alignItems: "center", flexWrap: "nowrap" }}>
              <OpProgress id={entry.id} name={entry.name} op={op} />
            </div>
          ) : (
            <>
              <div className="drawer-foot-row">
                <PrimaryAction entry={entry} size="lg" />
                {entry.state === "update-available" && !installed?.broken && (
                  <button className="btn btn-lg" onClick={() => launchApp(entry.id)}>
                    <Play size={16} strokeWidth={2.5} fill="currentColor" /> Launch
                  </button>
                )}
              </div>
              {(installed || catalog?.homepage) && (
                <div className="drawer-foot-row">
                  {installed && catalog && entry.pkg && (
                    <button
                      className="btn btn-ghost btn-sm"
                      onClick={() => installApp(entry.id, "repair")}
                      title="Download a fresh copy and reinstall, keeping your settings"
                    >
                      <Wrench size={14} strokeWidth={2.5} /> Repair
                    </button>
                  )}
                  {installed && (
                    <button className="btn btn-ghost btn-sm" onClick={() => openAppFolder(entry.id)}>
                      <FolderOpen size={14} strokeWidth={2.5} /> Open folder
                    </button>
                  )}
                  {catalog?.homepage && (
                    <button
                      className="btn btn-ghost btn-sm"
                      onClick={() => void openUrl(catalog.homepage!)}
                    >
                      <ExternalLink size={14} strokeWidth={2.5} /> Website
                    </button>
                  )}
                  {installed && (
                    <button
                      className="btn btn-danger btn-sm"
                      style={{ marginLeft: "auto" }}
                      onClick={() => requestUninstall(entry.id)}
                    >
                      <Trash2 size={14} strokeWidth={2.5} /> Uninstall
                    </button>
                  )}
                </div>
              )}
            </>
          )}
        </footer>
      </aside>
    </>
  );
}
