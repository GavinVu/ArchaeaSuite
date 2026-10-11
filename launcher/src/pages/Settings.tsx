import { useCallback, useEffect, useState } from "react";
import { ExternalLink, FolderOpen, Monitor, Moon, RefreshCw, Sun } from "lucide-react";
import { open } from "@tauri-apps/plugin-dialog";
import { openUrl } from "@tauri-apps/plugin-opener";
import { api, toError } from "../api";
import { formatBytes, percent } from "../format";
import {
  checkLauncherUpdate,
  installLauncherUpdate,
  saveSettings,
  toast,
  useStore,
} from "../store";
import type { StorageInfo, Theme } from "../types";

const REPO = "https://github.com/GavinVu/ArchaeaSuite";

const THEMES: { id: Theme; label: string; icon: typeof Sun }[] = [
  { id: "system", label: "System", icon: Monitor },
  { id: "dark", label: "Dark", icon: Moon },
  { id: "light", label: "Light", icon: Sun },
];

function Switch({
  checked,
  onChange,
  label,
}: {
  checked: boolean;
  onChange: (next: boolean) => void;
  label: string;
}) {
  return (
    <button
      className="switch"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
    />
  );
}

function LauncherUpdateSetting() {
  const launcher = useStore((s) => s.launcher);
  const version = useStore((s) => s.system?.launcherVersion ?? "");

  let help: string;
  switch (launcher.status) {
    case "checking":
      help = "Checking for a new launcher version…";
      break;
    case "up-to-date":
      help = `You have the latest version (${version}).`;
      break;
    case "available":
      help = `Version ${launcher.version} is ready to install. The launcher restarts to finish.`;
      break;
    case "installing":
      help = `Downloading ${launcher.version}${
        launcher.total ? ` — ${percent(launcher.done ?? 0, launcher.total)}%` : ""
      }. The launcher restarts when it's done.`;
      break;
    case "error":
      help = `Couldn't check: ${launcher.error}`;
      break;
    default:
      help = `Version ${version}. Launcher updates are signed and separate from app updates.`;
  }

  const canInstall = launcher.status === "available";
  return (
    <div className="setting">
      <div className="setting-text">
        <div className="setting-label">Launcher version</div>
        <div className="setting-help">{help}</div>
      </div>
      <div className="setting-actions">
        {canInstall ? (
          <button className="btn btn-primary" onClick={() => void installLauncherUpdate()}>
            Update launcher
          </button>
        ) : (
          <button
            className="btn"
            onClick={() => void checkLauncherUpdate()}
            disabled={launcher.status === "checking" || launcher.status === "installing"}
          >
            <RefreshCw
              size={15}
              strokeWidth={2.5}
              className={launcher.status === "checking" ? "spin" : undefined}
            />
            Check
          </button>
        )}
      </div>
    </div>
  );
}

function Storage({ installDir }: { installDir: string }) {
  const installedCount = useStore((s) => s.installed.length);
  const [info, setInfo] = useState<StorageInfo | null>(null);

  const load = useCallback(() => {
    api.storage().then(setInfo).catch(() => setInfo(null));
  }, []);
  useEffect(load, [load, installDir, installedCount]);

  const clear = async () => {
    try {
      await api.clearCache();
      toast("success", "Cache cleared");
      load();
    } catch (e) {
      toast("error", "Couldn't clear the cache", toError(e).message);
    }
  };

  const total = info?.totalBytes ?? 0;
  const free = info?.freeBytes ?? 0;
  const apps = info?.appsBytes ?? 0;
  const other = Math.max(0, total - free - apps);
  const width = (n: number) => (total ? `${Math.max(n > 0 ? 0.6 : 0, (n / total) * 100)}%` : "0%");

  return (
    <div className="panel">
      <div className="setting" style={{ display: "block" }}>
        <div className="setting-label">
          Disk {installDir.slice(0, 2).toUpperCase()}
          <span className="mono" style={{ fontWeight: 500, marginLeft: 10, color: "var(--text-dim)" }}>
            {info ? `${formatBytes(free)} free of ${formatBytes(total)}` : "Measuring…"}
          </span>
        </div>
        <div
          className="meter"
          style={{ marginTop: 12 }}
          role="img"
          aria-label={`ArchaeaSuite apps use ${formatBytes(apps)}; ${formatBytes(free)} free of ${formatBytes(total)}`}
        >
          <i className="is-apps" style={{ width: width(apps) }} />
          <i className="is-other" style={{ width: width(other) }} />
        </div>
        <div className="legend">
          <span>
            <i style={{ background: "var(--lime)" }} /> ArchaeaSuite apps {formatBytes(apps)}
          </span>
          <span>
            <i style={{ background: "var(--text-faint)" }} /> Other files {formatBytes(other)}
          </span>
          <span>
            <i style={{ background: "var(--bg)" }} /> Free {formatBytes(free)}
          </span>
        </div>
      </div>
      {info?.apps.map((a) => (
        <div className="setting" key={a.id} style={{ padding: "11px 18px" }}>
          <div className="setting-text">
            <div className="setting-label" style={{ fontWeight: 600 }}>
              {a.name}
            </div>
          </div>
          <span className="mono" style={{ fontSize: 12.5 }}>
            {formatBytes(a.bytes)}
          </span>
        </div>
      ))}
      <div className="setting">
        <div className="setting-text">
          <div className="setting-label">Launcher cache</div>
          <div className="setting-help">
            The saved catalog and temporary downloads —{" "}
            <span className="mono">{formatBytes(info?.cacheBytes ?? 0)}</span>. Clearing it never
            removes an installed app.
          </div>
        </div>
        <div className="setting-actions">
          <button className="btn" onClick={() => void clear()} disabled={!info?.cacheBytes}>
            Clear cache
          </button>
        </div>
      </div>
    </div>
  );
}

export function Settings() {
  const settings = useStore((s) => s.settings);
  const system = useStore((s) => s.system);
  if (!settings || !system) return <div className="page" />;

  const chooseDir = async () => {
    try {
      const picked = await open({
        directory: true,
        multiple: false,
        defaultPath: settings.installDir,
        title: "Choose where ArchaeaSuite apps are installed",
      });
      if (typeof picked === "string" && picked !== settings.installDir) {
        if (await saveSettings({ installDir: picked })) {
          toast("success", "Installation folder changed", "New apps will be installed there.");
        }
      }
    } catch (e) {
      toast("error", "Couldn't open the folder picker", toError(e).message);
    }
  };

  const isDefaultDir =
    settings.installDir.toLowerCase() === system.defaultInstallDir.toLowerCase();

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1 className="page-title">Settings</h1>
        </div>
      </header>

      <section className="section" style={{ marginTop: 0 }} aria-labelledby="set-install">
        <div className="section-head">
          <h2 className="section-title" id="set-install">
            Installation
          </h2>
        </div>
        <div className="panel">
          <div className="setting">
            <div className="setting-text">
              <div className="setting-label">Installation folder</div>
              <div className="setting-help">
                New apps are installed here. Apps you already have stay where they are.
              </div>
              <div className="path selectable">{settings.installDir}</div>
            </div>
            <div className="setting-actions" style={{ flexDirection: "column" }}>
              <button className="btn" onClick={() => void chooseDir()}>
                Change
              </button>
              <button
                className="btn btn-ghost"
                onClick={() =>
                  api
                    .openInstallDir()
                    .catch((e) => toast("error", "Couldn't open the folder", toError(e).message))
                }
              >
                <FolderOpen size={15} strokeWidth={2.5} /> Open
              </button>
              {!isDefaultDir && (
                <button
                  className="btn btn-ghost"
                  onClick={() => void saveSettings({ installDir: system.defaultInstallDir })}
                >
                  Reset
                </button>
              )}
            </div>
          </div>
        </div>
      </section>

      <section className="section" aria-labelledby="set-look">
        <div className="section-head">
          <h2 className="section-title" id="set-look">
            Appearance
          </h2>
        </div>
        <div className="panel">
          <div className="setting">
            <div className="setting-text">
              <div className="setting-label">Theme</div>
              <div className="setting-help">System follows your Windows light or dark mode.</div>
            </div>
            <div className="segmented" role="radiogroup" aria-label="Theme">
              {THEMES.map(({ id, label, icon: Icon }) => (
                <button
                  key={id}
                  role="radio"
                  aria-checked={settings.theme === id}
                  onClick={() => void saveSettings({ theme: id })}
                >
                  <Icon size={14} strokeWidth={2.5} aria-hidden="true" />
                  {label}
                </button>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section className="section" aria-labelledby="set-updates">
        <div className="section-head">
          <h2 className="section-title" id="set-updates">
            Updates
          </h2>
        </div>
        <div className="panel">
          <div className="setting">
            <div className="setting-text">
              <div className="setting-label">Check for updates automatically</div>
              <div className="setting-help">
                Looks for new app and launcher versions at startup and every few hours. Nothing is
                installed without your say-so.
              </div>
            </div>
            <Switch
              label="Check for updates automatically"
              checked={settings.autoCheckUpdates}
              onChange={(v) => void saveSettings({ autoCheckUpdates: v })}
            />
          </div>
          <div className="setting">
            <div className="setting-text">
              <div className="setting-label">Notify me about updates</div>
              <div className="setting-help">
                Shows one Windows notification when new app versions are found.
              </div>
            </div>
            <Switch
              label="Notify me about updates"
              checked={settings.notifyUpdates}
              onChange={(v) => void saveSettings({ notifyUpdates: v })}
            />
          </div>
          <LauncherUpdateSetting />
        </div>
      </section>

      <section className="section" aria-labelledby="set-storage">
        <div className="section-head">
          <h2 className="section-title" id="set-storage">
            Storage
          </h2>
        </div>
        <Storage installDir={settings.installDir} />
      </section>

      <section className="section" aria-labelledby="set-about">
        <div className="section-head">
          <h2 className="section-title" id="set-about">
            About ArchaeaSuite
          </h2>
        </div>
        <div className="panel about">
          <img className="for-dark" src="/brand/mark-inverse.png" alt="" />
          <img className="for-light" src="/brand/mark.png" alt="" />
          <div>
            <div className="about-name">ArchaeaSuite</div>
            <p>
              A free, open-source family of creative applications — an alternative to subscription
              software. No fees, no advertisements, no accounts, no paid features. Every app is
              released under the GNU General Public License v3, so it stays free for everyone.
            </p>
            <div className="card-meta" style={{ marginTop: 14 }}>
              <span className="tag">Launcher v{system.launcherVersion}</span>
              <span className="tag">{system.arch === "arm64" ? "Windows ARM64" : "Windows x64"}</span>
              <span className="tag">GPL-3.0-or-later</span>
            </div>
            <div className="about-links">
              <button className="btn btn-sm" onClick={() => void openUrl(REPO)}>
                <ExternalLink size={14} strokeWidth={2.5} /> Source code
              </button>
              <button className="btn btn-ghost btn-sm" onClick={() => void openUrl(`${REPO}/issues`)}>
                Report a problem
              </button>
              <button
                className="btn btn-ghost btn-sm"
                onClick={() => void openUrl(`${REPO}/blob/main/LICENSE`)}
              >
                License
              </button>
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}
