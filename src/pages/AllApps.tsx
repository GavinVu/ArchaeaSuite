import { useMemo, useState } from "react";
import { Search } from "lucide-react";
import { AppCard } from "../components/EntryActions";
import { refreshCatalog, useEntries, useStore } from "../store";
import type { AppEntry } from "../types";
import { OfflineBanner } from "./Home";

type Filter = "all" | "available" | "installed" | "soon";

const FILTERS: { id: Filter; label: string; test: (e: AppEntry) => boolean }[] = [
  { id: "all", label: "All", test: () => true },
  { id: "available", label: "Available", test: (e) => e.catalog?.status === "available" },
  { id: "installed", label: "Installed", test: (e) => e.installed !== null },
  { id: "soon", label: "Coming soon", test: (e) => e.state === "coming-soon" },
];

export function AllApps() {
  const entries = useEntries();
  const refreshing = useStore((s) => s.refreshing);
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<Filter>("all");

  const shown = useMemo(() => {
    const words = query.toLowerCase().split(/\s+/).filter(Boolean);
    const test = FILTERS.find((f) => f.id === filter)!.test;
    return entries.filter((e) => {
      if (!test(e)) return false;
      const haystack = `${e.name} ${e.tagline} ${e.description} ${e.category}`.toLowerCase();
      return words.every((w) => haystack.includes(w));
    });
  }, [entries, query, filter]);

  return (
    <div className="page">
      <OfflineBanner />
      <header className="page-head">
        <div>
          <h1 className="page-title">All Apps</h1>
          <p className="page-lede">
            Every ArchaeaSuite application. New apps appear here as they are released — no launcher
            update needed.
          </p>
        </div>
      </header>

      <div className="toolbar">
        <label className="search">
          <span className="sr-only">Search apps</span>
          <Search size={17} strokeWidth={2.5} aria-hidden="true" />
          <input
            type="search"
            placeholder="Search apps"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            spellCheck={false}
            autoComplete="off"
          />
        </label>
        <div className="chips" role="group" aria-label="Filter apps">
          {FILTERS.map((f) => (
            <button
              key={f.id}
              className="chip"
              aria-pressed={filter === f.id}
              onClick={() => setFilter(f.id)}
            >
              {f.label}
              <span className="mono" style={{ marginLeft: 7, opacity: 0.65 }}>
                {entries.filter(f.test).length}
              </span>
            </button>
          ))}
        </div>
      </div>

      {shown.length ? (
        <div className="card-grid">
          {shown.map((e) => (
            <AppCard key={e.id} entry={e} />
          ))}
        </div>
      ) : entries.length ? (
        <div className="empty">
          <h3>No matches</h3>
          <p>Nothing in the catalog matches that search.</p>
          <button
            className="btn"
            onClick={() => {
              setQuery("");
              setFilter("all");
            }}
          >
            Clear search
          </button>
        </div>
      ) : (
        <div className="empty">
          <h3>{refreshing ? "Loading catalog" : "Catalog unavailable"}</h3>
          <p>
            {refreshing
              ? "Fetching the list of apps from GitHub."
              : "The app list is downloaded from GitHub and saved for offline use. Connect to the internet and try again."}
          </p>
          {!refreshing && (
            <button className="btn btn-primary" onClick={() => void refreshCatalog()}>
              Try again
            </button>
          )}
        </div>
      )}
    </div>
  );
}
