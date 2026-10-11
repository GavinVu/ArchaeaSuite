import { RefreshCw } from "lucide-react";
import { AppIcon } from "../components/AppIcon";
import { Prose } from "../components/AppDrawer";
import { OpProgress, PrimaryAction, VersionJump } from "../components/EntryActions";
import { formatBytes, formatDate, formatRelative, percent } from "../format";
import {
  installLauncherUpdate,
  refreshCatalog,
  selectApp,
  updateAll,
  useEntries,
  useStore,
} from "../store";
import type { AppEntry } from "../types";
import { OfflineBanner } from "./Home";

export function UpdateRow({ entry, compact = false }: { entry: AppEntry; compact?: boolean }) {
  const op = useStore((s) => s.ops[entry.id]);
  const notes = entry.catalog?.releaseNotes;

  return (
    <div className="row row-block">
      <div className="row-line">
        <AppIcon name={entry.name} icon={entry.icon} size={compact ? 40 : 48} />
        <div className="row-main">
          <div className="row-name">
            <button className="row-link" onClick={() => selectApp(entry.id)}>
              {entry.name}
            </button>
            <VersionJump entry={entry} />
          </div>
          <div className="row-sub">
            {entry.pkg ? `${formatBytes(entry.pkg.size)} download` : ""}
            {entry.catalog?.releaseDate ? ` · released ${formatDate(entry.catalog.releaseDate)}` : ""}
          </div>
        </div>
        <div className="row-actions" style={op ? { flex: "0 1 320px", minWidth: 200 } : undefined}>
          {op ? (
            <OpProgress id={entry.id} name={entry.name} op={op} />
          ) : (
            <PrimaryAction entry={entry} />
          )}
        </div>
      </div>
      {!compact && notes && (
        <div style={{ paddingLeft: 64 }}>
          <div className="sub-title">Release notes</div>
          <Prose text={notes} />
        </div>
      )}
    </div>
  );
}

/** The launcher's own update — signed and delivered separately from apps. */
function LauncherUpdateBanner() {
  const launcher = useStore((s) => s.launcher);
  if (launcher.status !== "available" && launcher.status !== "installing") return null;
  const installing = launcher.status === "installing";
  const pct = percent(launcher.done ?? 0, launcher.total ?? 0);

  return (
    <div className="banner is-accent" role="status">
      <div className="banner-body">
        <strong>ArchaeaSuite Launcher {launcher.version} is available.</strong>{" "}
        {installing
          ? `Downloading${launcher.total ? ` ${pct}%` : ""} — the launcher restarts when it's done.`
          : "The launcher restarts to finish. Your installed apps are not affected."}
      </div>
      <button
        className="btn btn-sm"
        onClick={() => void installLauncherUpdate()}
        disabled={installing}
      >
        {installing ? "Updating" : "Update launcher"}
      </button>
    </div>
  );
}

export function Updates() {
  const entries = useEntries();
  const refreshing = useStore((s) => s.refreshing);
  const fetchedAt = useStore((s) => s.catalogFetchedAt);
  const busy = useStore((s) => Object.keys(s.ops).length > 0);
  const updates = entries.filter((e) => e.state === "update-available");
  const installedCount = entries.filter((e) => e.installed).length;

  return (
    <div className="page">
      <OfflineBanner />
      <header className="page-head">
        <div>
          <h1 className="page-title">Updates</h1>
          <p className="page-lede">
            Updates are verified before they replace anything. If one fails, the version you have
            stays exactly as it was.
          </p>
        </div>
        <div style={{ display: "flex", gap: 8, flex: "none" }}>
          {updates.length > 1 && (
            <button className="btn btn-primary" onClick={() => void updateAll()} disabled={busy}>
              Update all
            </button>
          )}
          <button className="btn" onClick={() => void refreshCatalog()} disabled={refreshing}>
            <RefreshCw size={15} strokeWidth={2.5} className={refreshing ? "spin" : undefined} />
            {refreshing ? "Checking" : "Check now"}
          </button>
        </div>
      </header>

      <LauncherUpdateBanner />

      {updates.length ? (
        <div className="rows">
          {updates.map((e) => (
            <UpdateRow key={e.id} entry={e} />
          ))}
        </div>
      ) : (
        <div className="empty">
          <h3>{installedCount ? "Everything is up to date" : "Nothing to update"}</h3>
          <p>
            {installedCount
              ? `All ${installedCount} installed app${installedCount > 1 ? "s are" : " is"} on the latest version.`
              : "Updates for the apps you install will appear here."}
          </p>
        </div>
      )}

      <p className="section-count" style={{ marginTop: 16 }}>
        Last checked: {formatRelative(fetchedAt)}
      </p>
    </div>
  );
}
