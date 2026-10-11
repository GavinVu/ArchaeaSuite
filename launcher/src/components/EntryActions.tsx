import { ArrowRight, Download, Play, RefreshCw, Wrench, X } from "lucide-react";
import { formatBytes, percent } from "../format";
import { cancelOp, installApp, launchApp, selectApp, useStore } from "../store";
import type { AppEntry, Op } from "../types";
import { AppIcon } from "./AppIcon";

const KIND_LABEL: Record<Op["kind"], string> = {
  install: "Installing",
  update: "Updating",
  repair: "Repairing",
  uninstall: "Uninstalling",
};

function describe(op: Op): { label: string; value: number | null; detail: string } {
  switch (op.phase) {
    case "downloading":
      return {
        label: "Downloading",
        value: op.total ? percent(op.done, op.total) : null,
        detail: op.total
          ? `${formatBytes(op.done)} / ${formatBytes(op.total)}`
          : formatBytes(op.done),
      };
    case "verifying":
      return { label: "Verifying", value: null, detail: "SHA-256" };
    case "extracting":
      return {
        label: KIND_LABEL[op.kind],
        value: op.total ? percent(op.done, op.total) : null,
        detail: op.total ? `${op.done} / ${op.total} files` : "",
      };
    case "finalizing":
      return { label: "Finishing", value: null, detail: "" };
    case "removing":
      return { label: "Uninstalling", value: null, detail: "" };
    default:
      return { label: "Connecting", value: null, detail: "" };
  }
}

export function OpProgress({ id, name, op }: { id: string; name: string; op: Op }) {
  const { label, value, detail } = describe(op);
  // Once files are being swapped into place the operation must run to completion.
  const cancellable =
    op.kind !== "uninstall" && (op.phase === "starting" || op.phase === "downloading");
  return (
    <>
      <div className="progress">
        <div className="progress-head">
          <span>
            {label}
            {value !== null ? ` ${value}%` : "…"}
          </span>
          <span>{detail}</span>
        </div>
        <div
          className="progress-track"
          role="progressbar"
          aria-label={`${label} ${name}`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={value ?? undefined}
        >
          <div
            className={`progress-fill${value === null ? " is-indeterminate" : ""}`}
            style={value === null ? undefined : { width: `${value}%` }}
          />
        </div>
      </div>
      {cancellable && (
        <button
          className="btn btn-ghost btn-sm btn-icon"
          onClick={() => cancelOp(id)}
          aria-label={`Cancel ${label.toLowerCase()} ${name}`}
          title="Cancel"
        >
          <X size={15} strokeWidth={2.5} />
        </button>
      )}
    </>
  );
}

/** The single most useful action for an app in its current state. */
export function PrimaryAction({
  entry,
  size = "md",
}: {
  entry: AppEntry;
  size?: "sm" | "md" | "lg";
}) {
  const arch = useStore((s) => s.system?.arch ?? "x64");
  const cls = `btn btn-wide${size === "sm" ? " btn-sm" : size === "lg" ? " btn-lg" : ""}`;
  const icon = size === "lg" ? 17 : 15;

  if (entry.installed?.broken && entry.catalog) {
    return (
      <button className={`${cls} btn-primary`} onClick={() => installApp(entry.id, "repair")}>
        <Wrench size={icon} strokeWidth={2.5} /> Repair
      </button>
    );
  }
  switch (entry.state) {
    case "update-available":
      return (
        <button className={`${cls} btn-primary`} onClick={() => installApp(entry.id, "update")}>
          <RefreshCw size={icon} strokeWidth={2.5} /> Update
        </button>
      );
    case "installed":
      return (
        <button
          className={`${cls} btn-primary`}
          onClick={() => launchApp(entry.id)}
          disabled={entry.installed?.broken}
        >
          <Play size={icon} strokeWidth={2.5} fill="currentColor" /> Launch
        </button>
      );
    case "not-installed":
      return (
        <button className={`${cls} btn-primary`} onClick={() => installApp(entry.id, "install")}>
          <Download size={icon} strokeWidth={2.5} /> Install
        </button>
      );
    case "coming-soon":
      return (
        <button className={cls} disabled>
          Coming soon
        </button>
      );
    default:
      return (
        <button className={cls} disabled title={`No ${arch} build is published yet`}>
          Unavailable
        </button>
      );
  }
}

export function StateTag({ entry }: { entry: AppEntry }) {
  if (entry.installed?.broken) return <span className="tag tag-danger">Needs repair</span>;
  switch (entry.state) {
    case "update-available":
      return <span className="tag tag-accent">Update</span>;
    case "installed":
      return <span className="tag tag-installed">Installed</span>;
    case "coming-soon":
      return <span className="tag">In development</span>;
    case "unsupported":
      return <span className="tag">Not for this PC</span>;
    default:
      return null;
  }
}

export function VersionJump({ entry }: { entry: AppEntry }) {
  return (
    <span className="version-jump" aria-label={`Version ${entry.installed?.version} to ${entry.catalog?.version}`}>
      <span className="from">{entry.installed?.version}</span>
      <ArrowRight size={13} strokeWidth={2.5} aria-hidden="true" />
      <span className="to">{entry.catalog?.version}</span>
    </span>
  );
}

export function AppCard({ entry }: { entry: AppEntry }) {
  const op = useStore((s) => s.ops[entry.id]);
  const version = entry.installed?.version ?? entry.catalog?.version;
  const dim = entry.state === "coming-soon" || entry.state === "unsupported";

  return (
    <article className={`card${dim ? " is-dim" : ""}`}>
      <div className="card-top">
        <AppIcon name={entry.name} icon={entry.icon} />
        <div className="card-titles">
          <h3 className="card-name">
            <button className="card-open" onClick={() => selectApp(entry.id)}>
              {entry.name}
              <span className="sr-only"> — view details</span>
            </button>
          </h3>
          <p className="card-tagline">{entry.tagline}</p>
        </div>
      </div>
      <div className="card-meta">
        <StateTag entry={entry} />
        {version && <span className="tag">v{version}</span>}
        {entry.category && <span className="tag">{entry.category}</span>}
      </div>
      <div className="card-foot">
        {op ? (
          <OpProgress id={entry.id} name={entry.name} op={op} />
        ) : (
          <>
            <div className="grow">
              {entry.state === "update-available" ? (
                <VersionJump entry={entry} />
              ) : entry.state === "not-installed" && entry.pkg ? (
                <span className="mono" style={{ fontSize: 12, color: "var(--text-faint)" }}>
                  {formatBytes(entry.pkg.size)} download
                </span>
              ) : null}
            </div>
            {entry.state === "update-available" && !entry.installed?.broken && (
              <button
                className="btn btn-ghost btn-icon"
                onClick={() => launchApp(entry.id)}
                aria-label={`Launch ${entry.name}`}
                title="Launch current version"
              >
                <Play size={15} strokeWidth={2.5} fill="currentColor" />
              </button>
            )}
            <PrimaryAction entry={entry} />
          </>
        )}
      </div>
    </article>
  );
}
