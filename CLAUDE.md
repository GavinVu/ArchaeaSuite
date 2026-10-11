# ArchaeaSuite Launcher

Hub app for ArchaeaSuite, a free GPLv3 creative suite (planned: ArchaeaPhoto,
Vector, Cut, Motion, Layout, Audio). The launcher discovers, installs, updates,
repairs, uninstalls and launches suite apps. **Only the launcher exists so far;
do not build the creative apps in this repo.** `test-app/` (Archaea Hello) is a
stand-in used to test the pipeline.

Stack: Tauri 2, Rust backend, React 18 + TypeScript + Vite. Windows 10/11 x64
and Windows 11 ARM64. No paid services; everything is hosted on GitHub
(`GavinVu/ArchaeaSuite`).

## Commands

```powershell
npm run tauri dev                 # run (needs cargo on PATH: ~\.cargo\bin)
npm run typecheck                 # tsc
cd src-tauri; cargo test          # backend unit tests
npm run tauri build -- --config src-tauri/tauri.unsigned.conf.json   # local installer
npm run brand                     # regenerate brand assets from ArchaeaSuiteLogo.png
./scripts/build-test-app.ps1 -Version 1.2.0     # build + package the test app
node scripts/dev-server.mjs       # local catalog; pair with ARCHAEA_DEV_CATALOG
```

A plain `npm run tauri build` needs `TAURI_SIGNING_PRIVATE_KEY` (updater
artifacts are signed). The key lives outside the repo at
`~/.tauri/archaeasuite-launcher.key`; never commit it.

In PowerShell 5.1 do not append `2>&1` to cargo/npm commands — stderr becomes
a terminating error.

## Architecture

```
catalog/catalog.json  ──HTTPS──▶  catalog.rs ──▶ cache (offline) ──▶ UI
GitHub Release ZIP    ──HTTPS──▶  installer.rs: download+SHA-256 ▶ staging ▶ swap ▶ registry
```

### Backend (`src-tauri/src`)

| File | Role |
| --- | --- |
| `lib.rs` | Tauri setup, plugins, all `#[tauri::command]`s. Thin; logic lives below. |
| `trust.rs` | `TRUSTED_OWNERS`, `CATALOG_URL`, URL allow-list for packages, icons, redirects. |
| `catalog.rs` | Catalog types, per-entry validation, fetch, icon embedding as `data:` URLs, cache. |
| `installer.rs` | Download, verify, safe ZIP extraction, staged swap, rollback, uninstall, launch, crash recovery. |
| `registry.rs` | `installed.json` — what is installed; enough to launch offline. Install-folder marker. |
| `settings.rs` | `settings.json` — install dir, theme, update prefs. |
| `state.rs` | `Ctx` (shared state), `Paths`, HTTP client with redirect policy. |
| `util.rs` | Atomic JSON writes, `safe_relative_path`, sizes. |
| `error.rs` | `Error` enum; serialises to `{ code, message }` for the UI. |

Launcher data: `%LOCALAPPDATA%\org.archaeasuite.launcher\` (settings,
registry, `cache/`). Apps default to `%LOCALAPPDATA%\ArchaeaSuite\Apps\<id>\`.

### Frontend (`src`)

- `store.ts` — one external store (`useStore(selector)`) plus every action
  (`installApp`, `refreshCatalog`, `saveSettings`, …). Components do not call
  `invoke` directly except via `api.ts`.
- `types.ts` mirrors the Rust types; change both together (Rust uses
  `serde(rename_all = "camelCase")`).
- `buildEntries` merges catalog + registry into `AppEntry` with a `state`
  (`not-installed | installed | update-available | coming-soon | unsupported`).
  All pages render from that.
- `pages/` Home, AllApps, Installed, Updates, Settings; `components/` shared UI.
- `styles.css` — design tokens then components. `public/splash.css` +
  `index.html` hold the static splash shown before React loads.

## Invariants — keep these true

- **Trust:** packages are only `https://github.com/<trusted owner>/<repo>/releases/download/<tag>/<file>`;
  redirects only to `github.com` / `*.githubusercontent.com`. `ARCHAEA_DEV_CATALOG`
  relaxes this for loopback in **debug builds only**.
- **Integrity:** nothing is extracted before size and SHA-256 match the catalog.
- **Extraction:** every ZIP entry path goes through `safe_relative_path`;
  symlinks and duplicate entries are errors, not skips.
- **Atomicity:** never modify an install in place. Extract to
  `<root>/.staging`, move the old install to `<root>/.backup`, move staging in,
  save the registry; any failure restores the backup. `recover()` cleans up at
  startup.
- **Deletion:** only delete a folder containing `.archaea-app.json` with the
  matching id.
- **Catalog-driven:** adding or updating an app must never require a launcher
  rebuild. Unknown JSON fields are ignored; breaking format changes bump
  `schemaVersion`. A bad entry is skipped, not fatal.
- **Offline:** installed apps list and launch from the registry alone.
- **Self-update is separate:** `tauri-plugin-updater` + signed `latest.json`
  from this repo's latest release. App releases in this repo must use
  `--latest=false`.
- No decorative controls: every button performs a real operation.

## Design language

Neo-brutalist, shared by all future suite apps: near-black `#161616`, charcoal
`#242424`, electric lime `#D6FF00`, off-white `#F5F5F0`. 2px borders, flat
fills, hard offset shadows on hover, 3–5px radii, heavy wide Archivo for
display type, IBM Plex Mono for versions and metadata. Lime is a fill or a
line — on the light theme never use it as text colour. Motion stays under
~150ms and respects `prefers-reduced-motion`. Fonts are bundled (no network).

The logo PNG's black parts vanish on dark backgrounds, so
`scripts/make-brand-assets.mjs` derives `mark.png` (light) and
`mark-inverse.png` (dark) plus the app icon. The wordmark is set in live type.

## Publishing

`docs/PUBLISHING.md` covers packaging an app, catalog fields, `preserve`, and
launcher releases (`launcher-v*` tag → `.github/workflows/release.yml`).
