#Requires -Version 7.0
<#
.SYNOPSIS
  Builds the portable exe and packages it for release: exe + notices + hashes,
  zipped. Unsigned on purpose (V2 decision D6) - document the SmartScreen
  warning in the release notes rather than implying it does not happen.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root 'dist/release/staging'
$releaseDir = Join-Path $root 'dist/release'

& (Join-Path $PSScriptRoot 'build-portable.ps1')
if ($LASTEXITCODE -ne 0) { throw 'portable build failed' }

$exe = Join-Path $root 'dist/BlurLink/BlurLink.exe'
if (-not (Test-Path $exe)) { throw "no portable exe at $exe" }

$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if ($fileVersion -notlike "$Version*") {
  throw "version mismatch: exe is $fileVersion, requested $Version (bump Directory.Build.props first)"
}

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item $exe $staging
Copy-Item (Join-Path $root 'LICENSE') $staging
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $staging

Push-Location $staging
try {
  $lines = Get-ChildItem -File | Where-Object Name -ne 'SHA256SUMS.txt' |
    ForEach-Object { "$((Get-FileHash -Algorithm SHA256 $_).Hash.ToLowerInvariant())  $($_.Name)" }
  $lines | Set-Content -Encoding ascii 'SHA256SUMS.txt'
} finally { Pop-Location }

$zip = Join-Path $releaseDir "BlurLink-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip

$zipHash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Write-Host "release: $zip"
Write-Host "sha256:  $zipHash"
