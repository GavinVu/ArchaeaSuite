# ArchaeaSuite Launcher

**ArchaeaSuite** is a free, open-source family of creative applications — an
alternative to subscription creative software. No fees, no ads, no accounts, no
paid features.

This repository is the **ArchaeaSuite Launcher**: the hub you install once and
then use to discover, install, update, repair, uninstall and launch every
ArchaeaSuite app.

| Planned app | Purpose |
| --- | --- |
| ArchaeaPhoto | Photo editing and compositing |
| ArchaeaVector | Vector illustration |
| ArchaeaCut | Video editing |
| ArchaeaMotion | Motion graphics and effects |
| ArchaeaLayout | Page layout |
| ArchaeaAudio | Audio editing |

None of the creative apps exist yet. The launcher ships with a small test app,
**Archaea Hello**, that exercises the complete install → launch → update →
uninstall lifecycle against real GitHub Releases.

## Install

Download the installer from the [latest release](https://github.com/GavinVu/ArchaeaSuite/releases/latest):

- `…_x64-setup.exe` for most PCs (Windows 10/11)
- `…_arm64-setup.exe` for Windows 11 on ARM

It installs for the current user without administrator rights and keeps itself
up to date. The installers are not yet code-signed, so Windows SmartScreen may
ask for confirmation the first time.

## What it does

- **Catalog from GitHub.** The app list is a JSON file in this repo, fetched at
  startup and cached. New apps appear without a launcher update.
- **Works offline.** Installed apps launch with no connection; the cached
  catalog keeps the rest of the UI usable.
- **Verified downloads.** Packages come only from GitHub Releases of trusted
  owners, over HTTPS, and must match the SHA-256 in the catalog.
- **Safe installs.** ZIPs are extracted with path-traversal, symlink and
  zip-bomb checks into a staging folder, then swapped into place. A failed
  update rolls back to the version you had.
- **Separate, signed self-update** for the launcher itself.
- Dark and light themes, keyboard navigation, reduced-motion support.

## Develop

Requirements: [Node.js](https://nodejs.org) 20+, [Rust](https://rustup.rs)
(stable, MSVC toolchain) and the Visual Studio C++ Build Tools.

```powershell
npm install
npm run tauri dev          # run the launcher
npm run typecheck          # TypeScript
cd src-tauri; cargo test   # backend tests
```

Build the installer (`src-tauri/target/release/bundle/nsis/*.exe`):

```powershell
# unsigned, for local use
npm run tauri build -- --config src-tauri/tauri.unsigned.conf.json

# signed update artifacts, as CI does
$env:TAURI_SIGNING_PRIVATE_KEY = Get-Content $HOME\.tauri\archaeasuite-launcher.key -Raw
npm run tauri build
```

ARM64: `rustup target add aarch64-pc-windows-msvc`, then add
`--target aarch64-pc-windows-msvc`.

To test the install pipeline against a local catalog instead of GitHub, see
[Testing before you publish](docs/PUBLISHING.md#testing-before-you-publish).

## Publishing apps

See [docs/PUBLISHING.md](docs/PUBLISHING.md) for how to package an app, add it
to the catalog and release launcher updates.

## Layout

```
catalog/        the hosted app catalog, its JSON schema and icons
src/            React + TypeScript UI
src-tauri/      Rust backend (catalog, installer, registry, settings)
test-app/       Archaea Hello, the end-to-end test application
scripts/        packaging, brand assets, local dev catalog server
docs/           publishing guide
```

## License

[GPL-3.0-or-later](LICENSE). ArchaeaSuite is and will remain free software.
