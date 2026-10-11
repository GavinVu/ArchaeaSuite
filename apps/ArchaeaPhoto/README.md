# ArchaeaPhoto

A layered photo editor for Windows 10/11 (x64 and ARM64). Part of
[ArchaeaSuite](../../README.md); installed and updated by the launcher, and
also runnable on its own (`ArchaeaPhoto.exe [files…]`).

Built with C#, [Avalonia](https://avaloniaui.net) 11 and
[SkiaSharp](https://github.com/mono/SkiaSharp); TIFF through
[LibTiff.NET](https://github.com/BitMiracle/libtiff.net). No network access,
no telemetry.

```powershell
dotnet run --project src/ArchaeaPhoto     # run from source
dotnet test                               # engine tests + UI tests on the headless platform
./build.ps1 [-Arch arm64] [-Version x.y.z] # self-contained build → ../../packages/*.zip
node tools/make-icons.mjs                 # regenerate Icons.g.cs (needs launcher/node_modules)
```

## Shortcuts

Press **F1** in the app for the full list. Tools: V move, M marquee (press
again for elliptical), L lasso, W magic wand, C crop, I eyedropper, B brush,
E eraser, S clone stamp, G bucket / gradient, T text, U shape, H hand, Z zoom.
Space-drag pans, Ctrl+scroll zooms, `[` `]` change brush size, X swaps
colours, D resets them.

## The .archa format

A ZIP archive: `document.json` (canvas size and the layer tree), one PNG per
layer in `layers/`, one greyscale PNG per mask in `masks/`, and `preview.png`.
See `src/ArchaeaPhoto.Core/Archa.cs`.

## Known limits of 0.1

- 8 bits per channel, sRGB only; no colour management or ICC profiles, and
  EXIF/metadata is not carried through to exported files.
- Adjustments and filters change pixels directly (with undo); there are no
  adjustment layers or smart filters yet.
- Groups always blend as one image ("isolated"); there is no pass-through mode.
- Text is laid out left-aligned without complex-script shaping, so Arabic,
  Indic scripts and emoji sequences will not render correctly.
- Copy and paste work between ArchaeaPhoto documents but not with other
  applications (no system clipboard images).
- Free transform scales, rotates and moves a whole layer; it ignores the
  selection and has no skew or perspective.
- No pressure sensitivity for pen tablets, no rulers or guides, no scrollbars
  (pan with Space-drag, the Hand tool or the mouse wheel).
- Images are limited to 16384 px on a side and must fit in memory; undo history
  is capped at 80 steps or about 1.5 GB.
- English only.
