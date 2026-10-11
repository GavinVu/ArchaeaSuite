# Publishing an app to ArchaeaSuite

The launcher learns about apps from one file: [`catalog/catalog.json`](../catalog/catalog.json)
on the `main` branch of this repository. Releasing a new app, or a new version
of one, means **uploading a ZIP to GitHub Releases and editing that file**. The
launcher is never rebuilt.

Everything here uses free GitHub features.

## 1. Build a package

A package is a plain ZIP of the app's install folder.

- The program to launch must be inside the ZIP. Its path **relative to the ZIP
  root** goes in the catalog's `executable` field (`ArchaeaPhoto.exe`, or
  `bin/ArchaeaPhoto.exe`). Do not wrap everything in a top-level folder unless
  you include that folder in `executable`.
- The app must be portable: it runs from wherever it is extracted, with no
  installer, no registry writes and no administrator rights.
- No symbolic links. Paths must be ordinary relative paths; the launcher
  rejects the whole package otherwise.
- Build one ZIP per architecture you support (`x64`, `arm64`). An ARM64 PC
  falls back to the `x64` package when there is no `arm64` one.

`scripts/package-app.ps1` builds a correct ZIP and prints the catalog fields:

```powershell
./scripts/package-app.ps1 -Source build\ArchaeaPhoto -Id archaea-photo `
    -Version 1.0.0 -Arch x64 -Repo GavinVu/ArchaeaPhoto -Tag v1.0.0
```

```json
{
  "url": "https://github.com/GavinVu/ArchaeaPhoto/releases/download/v1.0.0/archaea-photo-1.0.0-x64.zip",
  "sha256": "…",
  "size": 48211930,
  "installedSize": 131002214
}
```

## 2. Upload it to GitHub Releases

```powershell
gh release create v1.0.0 packages\archaea-photo-1.0.0-x64.zip `
    --repo GavinVu/ArchaeaPhoto --title "ArchaeaPhoto 1.0.0" --notes "First release."
```

The release must belong to a **trusted owner** — a GitHub account listed in
`TRUSTED_OWNERS` in [`src-tauri/src/trust.rs`](../src-tauri/src/trust.rs).
Any repository under a trusted owner works, so each app can have its own repo.

> If you publish an app release in *this* repository (as the test app does),
> add `--latest=false`. The launcher's self-updater reads this repository's
> "latest" release and expects it to be a launcher release.

Never replace a ZIP after it is published. The launcher verifies the SHA-256
from the catalog; a changed file fails verification for every user. Publish a
new version instead.

## 3. Add or update the catalog entry

```json
{
  "id": "archaea-photo",
  "name": "ArchaeaPhoto",
  "tagline": "Photo editing and image compositing.",
  "description": "Plain text. Blank lines separate paragraphs.\n- Lines starting with a dash are bullets",
  "icon": "icons/archaea-photo.png",
  "category": "Photo",
  "status": "available",
  "featured": true,
  "version": "1.0.0",
  "releaseDate": "2027-01-15",
  "releaseNotes": "What changed in this version.",
  "homepage": "https://github.com/GavinVu/ArchaeaPhoto",
  "executable": "ArchaeaPhoto.exe",
  "preserve": ["config"],
  "packages": {
    "x64": { "url": "…", "sha256": "…", "size": 48211930, "installedSize": 131002214 }
  }
}
```

| Field | Notes |
| --- | --- |
| `id` | Lowercase letters, digits, hyphens. Permanent: it is the install folder name and how the launcher recognises an installed app. |
| `status` | `available`, or `coming-soon` to list an app before it ships (then only `id` and `name` are required). |
| `version` | [Semantic version](https://semver.org). Raising it is what offers users an update. |
| `icon` | PNG, JPEG or WebP, at most 512 KB, 256×256 recommended. A path relative to `catalog.json`, or a full URL inside a trusted owner's repository. |
| `executable` | Must end in `.exe` and be a safe relative path. |
| `packages` | Keys are `x64` and/or `arm64`. `size` must be the exact byte size of the ZIP. |
| `preserve` | See below. |

The full format is described by [`catalog/catalog.schema.json`](../catalog/catalog.schema.json),
which CI checks on every push. An entry that fails the launcher's own
validation is skipped — the rest of the catalog still loads.

Commit the change to `main`. Launchers pick it up the next time they check
(at startup and every six hours; GitHub caches the raw file for about five
minutes).

## How updates treat user data

An update never modifies the installed app in place. The launcher downloads and
verifies the new package, extracts it to a staging folder beside the install,
then swaps the two folders. If any step fails — bad checksum, corrupt ZIP,
missing executable, the app still running — the staging folder is discarded
and the existing version is left exactly as it was.

Because the install folder is **replaced**, apps should keep projects, settings
and caches outside it, under `%APPDATA%\<AppName>` or the user's Documents
folder. That data is never touched by install, update, repair or uninstall.

For anything that must live inside the install folder, list it under
`preserve`. Those files and folders are copied from the old version into the
new one before the swap, overriding whatever the package ships at that path.
The test app keeps its launch counter this way (`"preserve": ["config"]`).

Uninstall removes the whole install folder, including `preserve` paths.

## Testing before you publish

You can exercise a package through the real install pipeline without
publishing anything:

```powershell
# terminal 1 — serves catalog/catalog.json and ./packages on 127.0.0.1:8765
node scripts/dev-server.mjs

# terminal 2 — a debug build that reads the local catalog
$env:ARCHAEA_DEV_CATALOG = "http://127.0.0.1:8765/catalog.json"
npm run tauri dev
```

The dev server rewrites package URLs, sizes and checksums to match the ZIPs in
`./packages`. Start it with `FAIL=checksum` to see a rejected download, or
`HELLO_VERSION=1.1.0` to serve the test app as an update. Release builds
ignore `ARCHAEA_DEV_CATALOG`.

## Releasing the launcher itself

Launcher updates are separate from app updates and are cryptographically
signed.

1. Bump `version` in `src-tauri/tauri.conf.json`, `src-tauri/Cargo.toml` and
   `package.json`.
2. Commit, then push a tag: `git tag launcher-v0.2.0 && git push --tags`.
3. The **Release launcher** workflow builds x64 and ARM64 installers, signs
   the update artifacts and publishes a release containing `latest.json`.

The workflow needs two repository secrets: `TAURI_SIGNING_PRIVATE_KEY` (the
contents of the private key file) and `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`.
The matching public key is in `tauri.conf.json` under `plugins.updater.pubkey`.
If the private key is lost, existing installs can no longer be updated
automatically — keep a backup somewhere safe and never commit it.
