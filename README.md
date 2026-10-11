# ArchaeaSuite

**ArchaeaSuite** is a free, open-source family of creative applications — an
alternative to subscription creative software. No fees, no ads, no accounts, no
paid features. Everything works offline.

| App | Purpose | Status |
| --- | --- | --- |
| **ArchaeaSuite Launcher** | Install, update and launch the suite | Available |
| **ArchaeaPhoto** | Photo editing and compositing | Preview (0.1) |
| ArchaeaVector | Vector illustration | Planned |
| ArchaeaCut | Video editing | Planned |
| ArchaeaMotion | Motion graphics and effects | Planned |
| ArchaeaLayout | Page layout | Planned |
| ArchaeaAudio | Audio editing | Planned |

## Install

Download the launcher:

- **[ArchaeaSuiteSetup.exe](https://github.com/GavinVu/ArchaeaSuite/releases/latest/download/ArchaeaSuiteSetup.exe)** for most PCs (Windows 10/11, x64)
- [ArchaeaSuiteSetup-arm64.exe](https://github.com/GavinVu/ArchaeaSuite/releases/latest/download/ArchaeaSuiteSetup-arm64.exe) for Windows 11 on ARM

These links always point at the newest version. The installer needs no
administrator rights, adds a Start Menu shortcut and an entry under Windows
Settings → Apps for uninstalling, and the launcher keeps itself up to date.
Uninstalling the launcher leaves installed apps and your projects in place. Install ArchaeaPhoto from inside the launcher. The installers are
not yet code-signed, so Windows SmartScreen may ask for confirmation the first
time.

## ArchaeaSuite Launcher

- **Catalog from GitHub.** The app list is a JSON file in this repo, fetched at
  startup and cached. New apps appear without a launcher update.
- **Works offline.** Installed apps launch with no connection.
- **Verified downloads.** Packages come only from GitHub Releases of trusted
  owners, over HTTPS, and must match the SHA-256 in the catalog.
- **Safe installs.** ZIPs are extracted with path-traversal, symlink and
  zip-bomb checks into a staging folder, then swapped into place. A failed
  update rolls back to the version you had.
- **Separate, signed self-update** for the launcher itself.

## ArchaeaPhoto

A layered raster editor (C#, Avalonia, SkiaSharp).

- **Documents:** new, open, save, export, image size, canvas size; PNG, JPEG,
  WebP and TIFF; layered `.archa` projects (an open ZIP + JSON + PNG format);
  several documents in tabs.
- **Layers:** create, delete, duplicate, reorder (drag or keys), rename,
  visibility, opacity, 17 blend modes, groups, masks, merge down, flatten.
- **Tools:** move, rectangular and elliptical marquee, lasso, magic wand, crop,
  eyedropper, brush, eraser, clone stamp, paint bucket, gradient, text, shapes,
  hand, zoom, free transform.
- **Adjustments:** brightness/contrast, levels, curves, exposure,
  hue/saturation, colour balance, invert, desaturate — with live preview.
- **Filters:** Gaussian blur, sharpen (unsharp mask), noise reduction.
- Undo/redo with a history panel, keyboard shortcuts (F1 lists them), dark and
  light themes.

Known limits of this preview are listed in [apps/ArchaeaPhoto/README.md](apps/ArchaeaPhoto/README.md).

## Develop

```
launcher/            the launcher (Tauri 2, Rust, React + TypeScript)
apps/ArchaeaPhoto/   the photo editor (.NET 9, Avalonia 11, SkiaSharp)
shared/              brand sources, fonts and scripts used by every app
catalog/             the hosted app catalog, its JSON schema and icons
docs/                publishing guide
```

**Launcher** — needs [Node.js](https://nodejs.org) 20+, [Rust](https://rustup.rs)
(stable, MSVC) and the Visual Studio C++ Build Tools.

```powershell
cd launcher
npm install
npm run tauri dev                       # run
npm run typecheck                       # TypeScript
cd src-tauri; cargo test                # backend tests
npm run tauri build -- --config src-tauri/tauri.unsigned.conf.json   # local installer
```

**ArchaeaPhoto** — needs the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
cd apps/ArchaeaPhoto
dotnet run --project src/ArchaeaPhoto   # run
dotnet test                             # engine and UI tests
./build.ps1                             # self-contained build + launcher package
```

To test installing an app through the launcher without publishing anything,
and for release steps, see [docs/PUBLISHING.md](docs/PUBLISHING.md).

## License

[GPL-3.0-or-later](LICENSE). ArchaeaSuite is and will remain free software.
