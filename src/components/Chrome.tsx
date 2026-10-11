import { useEffect, useRef, useState } from "react";
import {
  Copy,
  Home,
  LayoutGrid,
  Minus,
  PackageCheck,
  RefreshCw,
  Settings as SettingsIcon,
  Square,
  X,
} from "lucide-react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  dismissToast,
  navigate,
  requestUninstall,
  uninstallApp,
  useEntries,
  useStore,
} from "../store";
import type { Page } from "../types";

export function TitleBar() {
  const offline = useStore((s) => s.catalogError !== null);
  const dev = useStore((s) => s.system?.devCatalog ?? false);
  const [maximized, setMaximized] = useState(false);

  useEffect(() => {
    const win = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    const sync = () => win.isMaximized().then(setMaximized).catch(() => {});
    void sync();
    win.onResized(sync).then((fn) => (unlisten = fn));
    return () => unlisten?.();
  }, []);

  const win = getCurrentWindow();
  return (
    <header className="titlebar">
      <div className="titlebar-drag" data-tauri-drag-region>
        <span className="titlebar-title">ArchaeaSuite Launcher</span>
        {offline && <span className="titlebar-pill is-offline">Offline</span>}
        {dev && <span className="titlebar-pill">Dev catalog</span>}
      </div>
      <div className="window-controls">
        <button onClick={() => void win.minimize()} aria-label="Minimize" tabIndex={-1}>
          <Minus size={15} strokeWidth={2.25} />
        </button>
        <button
          onClick={() => void win.toggleMaximize()}
          aria-label={maximized ? "Restore" : "Maximize"}
          tabIndex={-1}
        >
          {maximized ? <Copy size={12} strokeWidth={2.5} /> : <Square size={12} strokeWidth={2.5} />}
        </button>
        <button className="is-close" onClick={() => void win.close()} aria-label="Close" tabIndex={-1}>
          <X size={16} strokeWidth={2.25} />
        </button>
      </div>
    </header>
  );
}

const NAV: { page: Page; label: string; icon: typeof Home }[] = [
  { page: "home", label: "Home", icon: Home },
  { page: "apps", label: "All Apps", icon: LayoutGrid },
  { page: "installed", label: "Installed", icon: PackageCheck },
  { page: "updates", label: "Updates", icon: RefreshCw },
];

export function Sidebar() {
  const page = useStore((s) => s.page);
  const system = useStore((s) => s.system);
  const offline = useStore((s) => s.catalogError !== null);
  const launcherUpdate = useStore((s) => s.launcher.status === "available");
  const entries = useEntries();
  const updates =
    entries.filter((e) => e.state === "update-available").length + (launcherUpdate ? 1 : 0);

  const item = (p: Page, label: string, Icon: typeof Home, badge = 0) => (
    <button
      key={p}
      className="nav-item"
      aria-current={page === p ? "page" : undefined}
      onClick={() => navigate(p)}
      title={label}
    >
      <Icon size={18} strokeWidth={2.4} aria-hidden="true" />
      <span>{label}</span>
      {badge > 0 && (
        <b className="nav-badge" aria-label={`${badge} available`}>
          {badge}
        </b>
      )}
    </button>
  );

  return (
    <aside className="sidebar">
      <div className="brand">
        <img className="for-dark" src="/brand/mark-inverse.png" alt="" />
        <img className="for-light" src="/brand/mark.png" alt="" />
        <div className="brand-text">
          <div className="brand-word">
            Archaea<b>Suite</b>
          </div>
          <div className="brand-sub">Launcher</div>
        </div>
      </div>
      <nav className="nav" aria-label="Main">
        {NAV.map((n) => item(n.page, n.label, n.icon, n.page === "updates" ? updates : 0))}
        <div className="nav-spacer" />
        {item("settings", "Settings", SettingsIcon)}
      </nav>
      <div className="sidebar-foot">
        <div className="status-line">
          <i className={`status-dot${offline ? " is-offline" : ""}`} />
          <span className="foot-text">{offline ? "Offline" : "Online"}</span>
        </div>
        {system && (
          <div className="foot-text">
            v{system.launcherVersion} · {system.arch === "arm64" ? "ARM64" : "x64"}
          </div>
        )}
      </div>
    </aside>
  );
}

export function Toasts() {
  const toasts = useStore((s) => s.toasts);
  return (
    <div className="toasts" role="status" aria-live="polite">
      {toasts.map((t) => (
        <div key={t.id} className={`toast is-${t.tone}`}>
          <div className="toast-body">
            <div className="toast-title">{t.title}</div>
            {t.detail && <div className="toast-detail selectable">{t.detail}</div>}
          </div>
          <button className="toast-close" onClick={() => dismissToast(t.id)} aria-label="Dismiss">
            <X size={15} strokeWidth={2.5} />
          </button>
        </div>
      ))}
    </div>
  );
}

export function UninstallDialog() {
  const id = useStore((s) => s.confirmUninstallId);
  const app = useStore((s) => s.installed.find((a) => a.id === s.confirmUninstallId));
  const cancelRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!id) return;
    cancelRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        requestUninstall(null);
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [id]);

  if (!id || !app) return null;
  return (
    <div className="dialog-wrap" onClick={() => requestUninstall(null)}>
      <div
        className="dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="uninstall-title"
        aria-describedby="uninstall-body"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id="uninstall-title">Uninstall {app.name}?</h2>
        <p id="uninstall-body">
          This removes the application files from this computer. Projects and documents you saved
          elsewhere are not touched, and you can reinstall at any time.
        </p>
        <div className="dialog-actions">
          <button ref={cancelRef} className="btn" onClick={() => requestUninstall(null)}>
            Keep
          </button>
          <button className="btn btn-danger" onClick={() => void uninstallApp(id)}>
            Uninstall
          </button>
        </div>
      </div>
    </div>
  );
}
