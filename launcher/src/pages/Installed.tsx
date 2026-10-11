import { FolderOpen, Trash2, Wrench } from "lucide-react";
import { AppIcon } from "../components/AppIcon";
import { OpProgress, PrimaryAction, StateTag, VersionJump } from "../components/EntryActions";
import { formatBytes, formatDate } from "../format";
import {
  installApp,
  launchApp,
  navigate,
  openAppFolder,
  requestUninstall,
  selectApp,
  useEntries,
  useStore,
} from "../store";
import type { AppEntry } from "../types";
import { Play } from "lucide-react";

function InstalledRow({ entry }: { entry: AppEntry }) {
  const op = useStore((s) => s.ops[entry.id]);
  const inst = entry.installed!;
  const canRepair = entry.catalog !== null && entry.pkg !== null;

  return (
    <div className="row">
      <AppIcon name={entry.name} icon={entry.icon} size={48} />
      <div className="row-main">
        <div className="row-name">
          <button className="row-link" onClick={() => selectApp(entry.id)}>
            {entry.name}
          </button>
          <StateTag entry={entry} />
        </div>
        <div className="row-sub">
          {entry.state === "update-available" ? (
            <VersionJump entry={entry} />
          ) : (
            <>v{inst.version}</>
          )}
          {" · "}
          {formatBytes(inst.sizeBytes)} · installed {formatDate(inst.installedAt)}
        </div>
      </div>
      <div className="row-actions" style={op ? { flex: "0 1 320px", minWidth: 200 } : undefined}>
        {op ? (
          <OpProgress id={entry.id} name={entry.name} op={op} />
        ) : (
          <>
            <button
              className="btn btn-ghost btn-icon"
              onClick={() => openAppFolder(entry.id)}
              aria-label={`Open ${entry.name} folder`}
              title="Open install folder"
            >
              <FolderOpen size={15} strokeWidth={2.5} />
            </button>
            <button
              className="btn btn-ghost btn-icon"
              onClick={() => installApp(entry.id, "repair")}
              disabled={!canRepair}
              aria-label={`Repair ${entry.name}`}
              title={
                canRepair
                  ? "Repair: download a fresh copy and reinstall"
                  : "Repair needs the online catalog"
              }
            >
              <Wrench size={15} strokeWidth={2.5} />
            </button>
            <button
              className="btn btn-danger btn-icon"
              onClick={() => requestUninstall(entry.id)}
              aria-label={`Uninstall ${entry.name}`}
              title="Uninstall"
            >
              <Trash2 size={15} strokeWidth={2.5} />
            </button>
            {entry.state === "update-available" && !inst.broken && (
              <button className="btn" onClick={() => launchApp(entry.id)}>
                <Play size={15} strokeWidth={2.5} fill="currentColor" /> Launch
              </button>
            )}
            <PrimaryAction entry={entry} />
          </>
        )}
      </div>
    </div>
  );
}

export function Installed() {
  const entries = useEntries().filter((e) => e.installed);
  const total = entries.reduce((sum, e) => sum + (e.installed?.sizeBytes ?? 0), 0);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1 className="page-title">Installed</h1>
          <p className="page-lede">
            Apps on this computer. They launch without an internet connection.
          </p>
        </div>
        {entries.length > 0 && (
          <span className="tag">
            {entries.length} app{entries.length > 1 ? "s" : ""} · {formatBytes(total)}
          </span>
        )}
      </header>

      {entries.length ? (
        <div className="rows">
          {entries.map((e) => (
            <InstalledRow key={e.id} entry={e} />
          ))}
        </div>
      ) : (
        <div className="empty">
          <h3>No apps installed</h3>
          <p>Install an app from the catalog and manage it from here.</p>
          <button className="btn btn-primary" onClick={() => navigate("apps")}>
            Browse apps
          </button>
        </div>
      )}
    </div>
  );
}
