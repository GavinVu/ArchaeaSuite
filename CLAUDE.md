# ArchaeaSuite

A free GPLv3 creative suite for Windows 10/11 (x64 and ARM64). No paid
services; everything is hosted on GitHub (`GavinVu/ArchaeaSuite`).

```
launcher/            ArchaeaSuite Launcher — Tauri 2, Rust, React 18 + TypeScript + Vite
apps/ArchaeaPhoto/   ArchaeaPhoto — C# (.NET 9), Avalonia 11, SkiaSharp 2.88
shared/              brand/ (logo PNGs), fonts/ (Archivo, IBM Plex Mono), scripts/
catalog/             catalog.json + schema + icons. STAYS AT THE REPO ROOT: installed
                     launchers fetch raw.githubusercontent.com/.../main/catalog/catalog.json
docs/PUBLISHING.md   packaging, catalog fields, release steps
packages/            built app ZIPs (git-ignored)
```

Planned, not started: ArchaeaVector, Cut, Motion, Layout, Audio. Do not build
them unless asked. `launcher/test-app` (Archaea Hello) is a stand-in for testing
the install pipeline.

## Commands

```powershell
# launcher (cargo must be on PATH: ~\.cargo\bin)
cd launcher
npm run tauri dev
npm run typecheck
cd src-tauri; cargo test
npm run tauri build -- --config src-tauri/tauri.unsigned.conf.json   # local installer
npm run brand                     # regenerate brand assets for launcher AND ArchaeaPhoto

# ArchaeaPhoto (dotnet is in C:\Program Files\dotnet)
cd apps/ArchaeaPhoto
dotnet run --project src/ArchaeaPhoto
dotnet test                       # 31 engine tests + 7 headless UI tests
./build.ps1 [-Arch arm64] [-Version x.y.z]   # publish + package → /packages
node tools/make-icons.mjs         # regenerate Icons.g.cs from lucide

# local install/update test (no publishing)
node shared/scripts/dev-server.mjs            # PHOTO_VERSION=0.1.1 serves an update
$env:ARCHAEA_DEV_CATALOG = "http://127.0.0.1:8765/catalog.json"; cd launcher; npm run tauri dev
```

- A plain `npm run tauri build` needs `TAURI_SIGNING_PRIVATE_KEY`; the key is
  at `~/.tauri/archaeasuite-launcher.key`. Never commit it.
- The launcher is single-instance. If the installed one is running, start the
  debug build with `-- --config '{\"identifier\":\"org.archaeasuite.launcher.dev\"}'`.
- Drive the launcher UI from a script: set
  `$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9222"`,
  then `node launcher/scripts/cdp.mjs eval "<js>"` / `shot out.png`.
- Screenshots of ArchaeaPhoto: set `ARCHAEAPHOTO_SHOTS=<dir>` and run
  `dotnet test`; the UI tests save rendered frames there.
- PowerShell 5.1: do not append `2>&1` to cargo/npm/dotnet commands.
- After moving a Rust project, `cargo clean` (the target dir caches absolute paths).

## Design language (both apps)

Neo-brutalist: near-black `#161616`, charcoal `#242424`, electric lime
`#D6FF00`, off-white `#F5F5F0`. 2px borders, flat fills, hard offset shadows on
hover, 3–5px radii. Archivo for UI, Archivo Expanded Black for display type,
IBM Plex Mono (uppercase, tracked) for labels and values. Lime is a fill or a
line — on the light theme never use it as text colour. Motion under ~150ms.
Fonts are bundled, no network.

Source of truth: `launcher/src/styles.css` (tokens at the top).
`apps/ArchaeaPhoto/src/ArchaeaPhoto/App.axaml` mirrors those tokens as
resources; change both together. The logo PNGs' black parts vanish on dark
backgrounds, so `launcher/scripts/make-brand-assets.mjs` derives light and
inverse emblems and the icons; wordmarks are set in live type.

## Launcher

Backend `launcher/src-tauri/src`: `lib.rs` (commands), `trust.rs` (trusted
owners, catalog URL, URL allow-list), `catalog.rs`, `installer.rs` (download,
verify, safe extraction, staged swap, rollback, uninstall, launch),
`registry.rs` (`installed.json`), `settings.rs`, `state.rs`, `util.rs`,
`error.rs`. Frontend `launcher/src`: `store.ts` (single store + all actions),
`api.ts`, `types.ts` (mirrors Rust types; change both), `pages/`, `components/`.

Invariants — keep these true:

- **Trust:** packages only from `https://github.com/<trusted owner>/<repo>/releases/download/<tag>/<file>`;
  redirects only to `github.com` / `*.githubusercontent.com`.
  `ARCHAEA_DEV_CATALOG` relaxes this for loopback in debug builds only.
- **Integrity:** nothing is extracted before size and SHA-256 match the catalog.
- **Extraction:** every ZIP path goes through `safe_relative_path`; symlinks and
  duplicates are errors.
- **Atomicity:** extract to `<root>/.staging`, move the old install to
  `<root>/.backup`, move staging in, save the registry; any failure restores.
- **Deletion:** only delete a folder containing `.archaea-app.json` with the matching id.
- **Catalog-driven:** adding or updating an app never needs a launcher rebuild.
- **Offline:** installed apps list and launch from the registry alone.
- **Self-update is separate** (`tauri-plugin-updater`, signed `latest.json` from
  the repo's latest release). App releases must use `--latest=false`.
- No decorative controls: every button performs a real operation.

## ArchaeaPhoto

`src/ArchaeaPhoto.Core` — the engine, no UI dependency:

| File | Role |
| --- | --- |
| `Px.cs` | Pixel helpers. Colour bitmaps are BGRA8888 premultiplied; masks and selections are Alpha8. |
| `Layer.cs`, `Document.cs` | Layer tree (raster, group, optional mask), selection, cached composite, layer edits. |
| `History.cs` | Undo: `SnapshotItem` (layer tree before/after, sharing bitmaps) and `PatchItem` (bytes of one rectangle). |
| `Compositor.cs` | Draws the stack with Skia blend modes; groups are isolated. |
| `Sel.cs` | Selection masks, combine modes, outline, flood fill. |
| `Painting.cs` | `Stroke` (brush/eraser/clone), `PixelEditSession` (preview + commit for adjustments/filters). |
| `Draw.cs` | Fill, bucket, gradient, shapes, text. |
| `Adjustments.cs`, `Filters.cs` | Colour adjustments (LUT or per-pixel) and blur/sharpen/denoise. |
| `Transforms.cs` | Layer and canvas transforms, crop, resize. |
| `ImageIO.cs`, `Archa.cs` | PNG/JPEG/WebP (Skia), TIFF (LibTiff.NET), `.archa` projects (ZIP of JSON + PNGs). |

`src/ArchaeaPhoto` — Avalonia UI, built in code (only `App.axaml` is XAML):
`Editor.cs` (app state), `CanvasView.cs` (viewport, input), `Tools.cs` +
`ToolsEdit.cs` (one class per tool), `Panels.cs`, `Dialogs.cs`,
`MainWindow.cs` (layout, keys) + `MainWindow.Commands.cs` (menus, file and
image commands), `Ui.cs` (builders, `Icon`, dialog chrome).

Rules that are easy to break:

- **History and bitmaps.** A `Document.Edit(...)` action must *replace* any
  bitmap it changes; snapshots share bitmap objects with the live document.
  In-place pixel edits go through `BeginPaint`/`EndPaint` (or `Stroke`,
  `Draw.Paint`, `PixelEditSession`). Never replace or grow a live bitmap
  outside one of those — a later redo would then write into a different bitmap
  than later snapshots reference.
- **Masks are not images.** Draw an Alpha8 bitmap with `Px.DrawMask`, not
  `Px.Draw`: Skia treats an alpha-only image as coverage, so DstIn/Src do
  nothing where the mask is zero.
- **No full-bitmap copies per frame.** Use `Px.Draw` (wraps pixels) rather than
  `SKCanvas.DrawBitmap`; the canvas redraws only the invalidated rectangle.
- **Coordinates.** `CanvasView` works in physical pixels; Avalonia input is in
  DIPs. The frame bitmap is created at 96 dpi on purpose.
- **Nothing in the install folder.** Settings live in
  `%APPDATA%\ArchaeaSuite\ArchaeaPhoto` (`ARCHAEAPHOTO_DATA_DIR` overrides, used
  by tests); projects go where the user saves them. Uninstall and update must
  never touch user files.
- Every command and tool does real work; when one cannot run (no pixel layer
  selected), say why in the status bar instead of failing silently.
- Version lives in `Directory.Build.props` and must match the catalog entry.

Known limits are listed in `apps/ArchaeaPhoto/README.md`.

## Publishing

`docs/PUBLISHING.md`. In short: build ZIPs → `node shared/scripts/catalog-set-package.mjs <id> <version>`
→ test with the dev server → `gh release create <id>-v<version> … --latest=false`
→ push the catalog. The release must exist before the catalog pointing at it is
pushed, and ZIPs must not be rebuilt in between (hashes would change).
Launcher releases: `launcher-v*` tag → `.github/workflows/release.yml`.
