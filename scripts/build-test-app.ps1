<#
.SYNOPSIS
  Builds the Archaea Hello test application at a given version and packages it.

.EXAMPLE
  ./scripts/build-test-app.ps1 -Version 1.1.0
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$target = if ($Arch -eq 'arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }

$env:HELLO_VERSION = $Version
try {
  cargo build --release --target $target --manifest-path (Join-Path $root 'test-app\Cargo.toml')
  if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
}
finally {
  Remove-Item Env:\HELLO_VERSION
}

$stage = Join-Path $root "test-app\target\stage-$Version-$Arch"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $root "test-app\target\$target\release\ArchaeaHello.exe") $stage
Set-Content -Path (Join-Path $stage 'README.txt') -Encoding utf8 -Value @"
Archaea Hello $Version
A test application for ArchaeaSuite Launcher. Licensed under GPL-3.0-or-later.
Source: https://github.com/GavinVu/ArchaeaSuite/tree/main/test-app
"@

& (Join-Path $PSScriptRoot 'package-app.ps1') -Source $stage -Id 'archaea-hello' -Version $Version -Arch $Arch
