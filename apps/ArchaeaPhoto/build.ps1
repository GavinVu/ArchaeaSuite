<#
.SYNOPSIS
  Publishes ArchaeaPhoto as a self-contained Windows application and packages
  it as an ArchaeaSuite app package (ZIP) for the launcher.

.DESCRIPTION
  Output: packages/archaea-photo-<version>-<arch>.zip at the repository root,
  plus the catalog fields (URL, SHA-256, sizes) printed for catalog.json.
  The version defaults to <Version> in Directory.Build.props; pass -Version to
  build a differently numbered package (for example to test an update).

.EXAMPLE
  ./apps/ArchaeaPhoto/build.ps1                 # x64, version from Directory.Build.props
  ./apps/ArchaeaPhoto/build.ps1 -Arch arm64
  ./apps/ArchaeaPhoto/build.ps1 -Version 0.1.1  # a second version, to test updating
#>
param(
  [string] $Version,
  [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
  [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$app = $PSScriptRoot
$repo = Resolve-Path (Join-Path $app '..\..')
if (-not $Version) {
  $Version = ([xml](Get-Content (Join-Path $app 'Directory.Build.props'))).Project.PropertyGroup.Version
}

if (-not $SkipTests) {
  dotnet test (Join-Path $app 'tests\ArchaeaPhoto.Tests') --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }
}

$out = Join-Path $app "publish\$Version-$Arch"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $app 'src\ArchaeaPhoto') -c Release -r "win-$Arch" --self-contained true `
  -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $out 'LICENSE.txt')
Copy-Item (Join-Path $app 'THIRD-PARTY-NOTICES.txt') $out
Set-Content -Path (Join-Path $out 'README.txt') -Encoding utf8 -Value @"
ArchaeaPhoto $Version ($Arch)
Photo editing and image compositing. Part of ArchaeaSuite.
Licensed under GPL-3.0-or-later. Source: https://github.com/GavinVu/ArchaeaSuite

Run ArchaeaPhoto.exe. Projects are saved wherever you choose (Documents\ArchaeaPhoto
by default) and settings in %APPDATA%\ArchaeaSuite\ArchaeaPhoto; neither is inside
this folder, so updating or removing the application never touches them.
"@

& (Join-Path $repo 'shared\scripts\package-app.ps1') -Source $out -Id 'archaea-photo' -Version $Version -Arch $Arch
