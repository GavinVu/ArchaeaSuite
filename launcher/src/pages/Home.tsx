import { RefreshCw } from "lucide-react";
import { AppCard } from "../components/EntryActions";
import { UpdateRow } from "./Updates";
import { formatRelative } from "../format";
import { navigate, refreshCatalog, useEntries, useStore } from "../store";

export function OfflineBanner() {
  const error = useStore((s) => s.catalogError);
  const fetchedAt = useStore((s) => s.catalogFetchedAt);
  const hasCatalog = useStore((s) => s.catalog !== null);
  const refreshing = useStore((s) => s.refreshing);
  if (!error) return null;
  return (
    <div className="banner is-warn" role="status">
      <div className="banner-body" title={error}>
        <strong>The app catalog can't be reached.</strong>{" "}
        {hasCatalog
          ? `Installed apps work as normal. Showing the catalog saved ${formatRelative(fetchedAt)}.`
          : "Installed apps work as normal. Connect to the internet to browse and install apps."}
      </div>
      <button className="btn btn-sm" onClick={() => void refreshCatalog()} disabled={refreshing}>
        <RefreshCw size={14} strokeWidth={2.5} className={refreshing ? "spin" : undefined} />
        Retry
      </button>
    </div>
  );
}

export function Home() {
  const entries = useEntries();
  const refreshing = useStore((s) => s.refreshing);
  const hasCatalog = useStore((s) => s.catalog !== null);

  const installed = entries.filter((e) => e.installed);
  const updates = entries.filter((e) => e.state === "update-available");
  const featured = entries.filter((e) => e.featured && !e.installed);
  const available = entries.filter((e) => e.catalog?.status === "available").length;

  return (
    <div className="page">
      <OfflineBanner />

      <section className="hero">
        <div>
          <span className="hero-kicker">Free · Open source · Yours</span>
          <h1 className="hero-title">
            Create without <em>the subscription.</em>
          </h1>
          <p className="hero-lede">
            ArchaeaSuite is a family of professional creative tools with no fees, no ads and no
            account. Install them here, keep them current, and launch them online or off.
          </p>
          <div className="hero-actions">
            <button className="btn btn-primary btn-lg" onClick={() => navigate("apps")}>
              Browse apps
            </button>
            <button
              className="btn btn-lg"
              onClick={() => void refreshCatalog()}
              disabled={refreshing}
            >
              <RefreshCw size={16} strokeWidth={2.5} className={refreshing ? "spin" : undefined} />
              {refreshing ? "Checking" : "Check for updates"}
            </button>
          </div>
        </div>
        <img className="hero-mark for-dark" src="/brand/mark-inverse.png" alt="" />
        <img className="hero-mark for-light" src="/brand/mark.png" alt="" />
      </section>

      <div className="stats">
        <button className="stat" onClick={() => navigate("installed")}>
          <span className="stat-value">{installed.length}</span>
          <span className="stat-label">Installed</span>
        </button>
        <button className="stat" onClick={() => navigate("updates")}>
          <span className={`stat-value${updates.length ? " is-hot" : ""}`}>{updates.length}</span>
          <span className="stat-label">Updates available</span>
        </button>
        <button className="stat" onClick={() => navigate("apps")}>
          <span className="stat-value">{available}</span>
          <span className="stat-label">Ready to install</span>
        </button>
      </div>

      {updates.length > 0 && (
        <section className="section" aria-labelledby="home-updates">
          <div className="section-head">
            <h2 className="section-title" id="home-updates">
              Available updates
            </h2>
            <button className="btn btn-ghost btn-sm" onClick={() => navigate("updates")}>
              View all
            </button>
          </div>
          <div className="rows">
            {updates.map((e) => (
              <UpdateRow key={e.id} entry={e} compact />
            ))}
          </div>
        </section>
      )}

      <section className="section" aria-labelledby="home-installed">
        <div className="section-head">
          <h2 className="section-title" id="home-installed">
            Your apps
          </h2>
          <span className="section-count">{installed.length} installed</span>
        </div>
        {installed.length ? (
          <div className="card-grid">
            {installed.map((e) => (
              <AppCard key={e.id} entry={e} />
            ))}
          </div>
        ) : (
          <div className="empty">
            <h3>Nothing installed yet</h3>
            <p>Pick an app from the catalog and it will show up here, ready to launch.</p>
            <button className="btn btn-primary" onClick={() => navigate("apps")}>
              Browse apps
            </button>
          </div>
        )}
      </section>

      {featured.length > 0 && (
        <section className="section" aria-labelledby="home-featured">
          <div className="section-head">
            <h2 className="section-title" id="home-featured">
              Featured
            </h2>
            <button className="btn btn-ghost btn-sm" onClick={() => navigate("apps")}>
              All apps
            </button>
          </div>
          <div className="card-grid">
            {featured.map((e) => (
              <AppCard key={e.id} entry={e} />
            ))}
          </div>
        </section>
      )}

      {!hasCatalog && !refreshing && installed.length === 0 && (
        <p className="page-lede" style={{ marginTop: 24 }}>
          The catalog hasn't been downloaded yet. It is fetched from GitHub the first time the
          launcher is online, then kept for offline use.
        </p>
      )}
    </div>
  );
}
