<#
.SYNOPSIS
  Builds ArchaeaIDE as a portable Windows application and packages it as an
  ArchaeaSuite app package (ZIP) for the launcher.

.DESCRIPTION
  Output: packages/archaea-ide-<version>-<arch>.zip at the repository root,
  plus the catalog fields (URL, SHA-256, sizes) printed for catalog.json.
  The version defaults to the one in src-tauri/tauri.conf.json; pass -Version
  to build a differently numbered package (for example to test an update).

.EXAMPLE
  ./apps/ArchaeaIDE/build.ps1                 # x64
  ./apps/ArchaeaIDE/build.ps1 -Arch arm64     # needs: rustup target add aarch64-pc-windows-msvc
  ./apps/ArchaeaIDE/build.ps1 -Version 0.1.1  # a second version, to test updating
#>
param(
  [string] $Version,
  [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
  [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$app = $PSScriptRoot
$repo = Resolve-Path (Join-Path $app '..\..')
$conf = Get-Content (Join-Path $app 'src-tauri\tauri.conf.json') -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $conf.version }
$target = if ($Arch -eq 'arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }

Push-Location $app
try {
  if (-not (Test-Path 'node_modules')) { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } }
  if (-not $SkipTests) {
    Push-Location 'src-tauri'
    try { cargo test --locked; if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' } }
    finally { Pop-Location }
  }
  # The version shown in the app comes from the Tauri config, so an override file is enough.
  $override = Join-Path $env:TEMP "archaea-ide-version.json"
  [IO.File]::WriteAllText($override, (@{ version = $Version } | ConvertTo-Json))
  npm run tauri build -- --no-bundle --target $target --config $override
  if ($LASTEXITCODE -ne 0) { throw 'tauri build failed.' }
}
finally { Pop-Location }

$out = Join-Path $app "publish\$Version-$Arch"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $app "src-tauri\target\$target\release\ArchaeaIDE.exe") $out
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $out 'LICENSE.txt')
Copy-Item (Join-Path $app 'THIRD-PARTY-NOTICES.txt') $out
Set-Content -Path (Join-Path $out 'README.txt') -Encoding utf8 -Value @"
ArchaeaIDE $Version ($Arch)
A code editor and IDE. Part of ArchaeaSuite.
Licensed under GPL-3.0-or-later. Source: https://github.com/GavinVu/ArchaeaSuite

Run ArchaeaIDE.exe. It needs the Microsoft Edge WebView2 Runtime, which Windows 11
includes and ArchaeaSuite Launcher installs. Settings, shortcuts and extensions live in
%APPDATA%\ArchaeaSuite\ArchaeaIDE and your projects wherever you keep them; neither is
inside this folder, so updating or removing the application never touches them.
"@

& (Join-Path $repo 'shared\scripts\package-app.ps1') -Source $out -Id 'archaea-ide' -Version $Version -Arch $Arch
