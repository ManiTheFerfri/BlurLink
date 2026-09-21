#Requires -Version 7.0
<#
.SYNOPSIS
  Development-only packaging: stages a runnable folder under dist/.
.DESCRIPTION
  Copies the Shell publish output + blurlink-net.exe + WinDivert binaries
  (when staged per third-party/WinDivert/README.md) + docs. Unsigned build:
  SmartScreen/UAC will warn — expected until binaries are code-signed.
#>
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist/BlurLink-dev'

Write-Host "==> publish Shell (self-contained: NO, framework-dependent)" -ForegroundColor Cyan
$pub = Join-Path $root 'out/publish'
& dotnet publish (Join-Path $root 'src/BlurLink.Shell/BlurLink.Shell.csproj') `
  -c $Configuration -r win-x64 --self-contained false -o $pub --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -Recurse -Force (Join-Path $pub '*') $dist

# Stage helper if built.
$helper = Get-ChildItem -Path (Join-Path $root 'out') -Recurse -Filter 'blurlink-net.exe' -ErrorAction SilentlyContinue |
  Select-Object -First 1
if ($helper) {
  Copy-Item -Force $helper.FullName (Join-Path $dist 'blurlink-net.exe')
} else {
  Write-Warning "blurlink-net.exe not found — run scripts/build.ps1 with MSVC first. Join mode will report a clear error."
}

# Stage WinDivert runtime binaries + license when present.
$wd = Join-Path $root 'third-party/WinDivert/x64'
foreach ($f in @('WinDivert.dll', 'WinDivert64.sys', 'LICENSE', 'COPYING', 'COPYING.LGPL')) {
  $src = Join-Path $wd $f
  if (Test-Path $src) { Copy-Item -Force $src (Join-Path $dist $f) }
}

foreach ($doc in @('README.md', 'THIRD-PARTY-NOTICES.md', 'TODO.md')) {
  Copy-Item -Force (Join-Path $root $doc) (Join-Path $dist $doc)
}
Copy-Item -Recurse -Force (Join-Path $root 'docs') (Join-Path $dist 'docs')
Copy-Item -Recurse -Force (Join-Path $root 'profiles') (Join-Path $dist 'profiles')

Write-Host "Dev package staged at $dist" -ForegroundColor Green
Write-Host "Unsigned build: expect SmartScreen/UAC warnings until code-signed." -ForegroundColor Yellow
