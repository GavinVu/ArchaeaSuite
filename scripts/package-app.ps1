<#
.SYNOPSIS
  Packages a built application folder into an ArchaeaSuite app package (ZIP)
  and prints the catalog fields for it.

.EXAMPLE
  ./scripts/package-app.ps1 -Source build/ArchaeaPhoto -Id archaea-photo -Version 1.0.0 -Arch x64 `
      -Repo GavinVu/ArchaeaPhoto -Tag v1.0.0
#>
param(
  [Parameter(Mandatory)] [string] $Source,
  [Parameter(Mandatory)] [string] $Id,
  [Parameter(Mandatory)] [string] $Version,
  [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
  [string] $OutDir = (Join-Path $PSScriptRoot '..\packages'),
  # Used only to print the download URL the catalog should contain.
  [string] $Repo = 'GavinVu/ArchaeaSuite',
  [string] $Tag = "$Id-v$Version"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Source = (Resolve-Path $Source).Path.TrimEnd('\')
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$zipName = "$Id-$Version-$Arch.zip"
$zipPath = Join-Path $OutDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath }

# Entries are written explicitly so names always use forward slashes and are
# relative to the package root (the launcher requires the executable path in
# the catalog to be relative to that root).
$installedSize = 0
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
  Get-ChildItem -Path $Source -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($Source.Length + 1).Replace('\', '/')
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
      $zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    $installedSize += $_.Length
  }
}
finally {
  $zip.Dispose()
}

$sha = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLower()
$size = (Get-Item $zipPath).Length

Write-Host "Package: $zipPath"
Write-Host ""
Write-Host "Catalog entry for packages.$Arch`:"
[ordered]@{
  url           = "https://github.com/$Repo/releases/download/$Tag/$zipName"
  sha256        = $sha
  size          = $size
  installedSize = $installedSize
} | ConvertTo-Json

# Also return the values so other scripts can consume them.
[pscustomobject]@{ Path = $zipPath; Name = $zipName; Sha256 = $sha; Size = $size; InstalledSize = $installedSize; Tag = $Tag } |
  Out-Null
