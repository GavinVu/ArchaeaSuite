// Mirrors the Rust types in src-tauri/src. Keep the two in sync.

export type AppStatus = "available" | "coming-soon";
export type Arch = "x64" | "arm64";
export type Theme = "system" | "dark" | "light";

export interface Package {
  url: string;
  sha256: string;
  size: number;
  installedSize?: number | null;
}

export interface CatalogApp {
  id: string;
  name: string;
  tagline: string;
  description: string;
  icon?: string | null;
  category: string;
  status: AppStatus;
  featured: boolean;
  version?: string | null;
  releaseDate?: string | null;
  releaseNotes?: string | null;
  homepage?: string | null;
  executable?: string | null;
  packages: Partial<Record<Arch, Package>>;
  preserve: string[];
}

export interface Catalog {
  schemaVersion: number;
  updated?: string | null;
  apps: CatalogApp[];
}

export interface CatalogResult {
  catalog: Catalog | null;
  source: "network" | "cache" | "none";
  fetchedAt: number | null;
  error: string | null;
  warnings: string[];
}

export interface InstalledApp {
  id: string;
  name: string;
  tagline: string;
  icon?: string | null;
  version: string;
  arch: string;
  installPath: string;
  executable: string;
  installedAt: number;
  sizeBytes: number;
  broken: boolean;
}

export interface Settings {
  installDir: string;
  theme: Theme;
  autoCheckUpdates: boolean;
  notifyUpdates: boolean;
}

export interface SystemInfo {
  arch: Arch;
  launcherVersion: string;
  defaultInstallDir: string;
  catalogUrl: string;
  devCatalog: boolean;
}

export interface StorageInfo {
  installDir: string;
  freeBytes: number | null;
  totalBytes: number | null;
  appsBytes: number;
  cacheBytes: number;
  apps: { id: string; name: string; bytes: number }[];
}

export interface Progress {
  id: string;
  phase: "downloading" | "verifying" | "extracting" | "finalizing";
  done: number;
  total: number;
}

export interface BackendError {
  code: string;
  message: string;
}

export type EntryState =
  | "not-installed"
  | "installed"
  | "update-available"
  | "coming-soon"
  | "unsupported";

/** A catalog entry merged with what is installed on this machine. */
export interface AppEntry {
  id: string;
  name: string;
  tagline: string;
  description: string;
  icon: string | null;
  category: string;
  featured: boolean;
  state: EntryState;
  catalog: CatalogApp | null;
  installed: InstalledApp | null;
  /** The package that would be downloaded on this machine, if any. */
  pkg: Package | null;
  pkgArch: Arch | null;
}

export type OpKind = "install" | "update" | "repair" | "uninstall";

export interface Op {
  kind: OpKind;
  phase: Progress["phase"] | "starting" | "removing";
  done: number;
  total: number;
}

export type Page = "home" | "apps" | "installed" | "updates" | "settings";
