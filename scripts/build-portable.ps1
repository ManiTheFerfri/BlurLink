#Requires -Version 7.0
<#
.SYNOPSIS
  Builds the BlurLink deliverable: ONE BlurLink.exe with the native helper
  AND the WinDivert runtime embedded (extracted to
  %LocalAppData%\BlurLink\bin on first launch).
.DESCRIPTION
  PROJECT DECISION (2026-09-09): slim-only. Default output is the
  framework-dependent single exe (~3.5 MB, needs .NET 10 Desktop Runtime).
  -Full keeps the legacy self-contained build (~140 MB); -Sfx wraps it.
  1. Ensures official WinDivert 2.2.2 x64 files (downloads them if missing).
  2. Links blurlink-net.exe (MinGW g++ or MSVC via CMake).
  3. Stages helper + WinDivert.dll + WinDivert64.sys into Native/ (git-ignored).
  4. Publishes BlurLink.exe as a single file (embeds all three).
  5. Copies JUST BlurLink.exe to dist/BlurLink/.
#>
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',
  # Full: legacy self-contained single exe (~140 MB, runs anywhere).
  [switch]$Full,
  # Sfx: wrap the Full build in a 7z self-extractor (~43 MB single exe,
  # +~2s cold start). Requires -Full; slim is already tiny.
  [switch]$Sfx
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $root 'src/BlurLink.Shell/Native'
$wdDir = Join-Path $root 'third-party/WinDivert/x64'

Write-Host '==> WinDivert runtime (official 2.2.2 x64)' -ForegroundColor Cyan
$dll = Join-Path $wdDir 'WinDivert.dll'
$sys = Join-Path $wdDir 'WinDivert64.sys'
if (-not (Test-Path $dll) -or -not (Test-Path $sys)) {
  Write-Host 'Downloading WinDivert-2.2.2-A.zip from reqrypt.org…' -ForegroundColor Yellow
  $zip = Join-Path ([System.IO.Path]::GetTempPath()) 'windivert-dl.zip'
  Invoke-WebRequest -Uri 'https://reqrypt.org/download/WinDivert-2.2.2-A.zip' -OutFile $zip
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) 'windivert-dl'
  if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
  Expand-Archive -LiteralPath $zip -DestinationPath $tmp
  New-Item -ItemType Directory -Force -Path $wdDir | Out-Null
  Copy-Item -Force (Join-Path $tmp 'WinDivert-2.2.2-A/x64/WinDivert.dll') $dll
  Copy-Item -Force (Join-Path $tmp 'WinDivert-2.2.2-A/x64/WinDivert64.sys') $sys
  if (-not (Test-Path (Join-Path $root 'third-party/WinDivert/LICENSE.Windivert'))) {
    Copy-Item -Force (Join-Path $tmp 'WinDivert-2.2.2-A/LICENSE') (Join-Path $root 'third-party/WinDivert/LICENSE.Windivert')
  }
}
foreach ($f in @($dll, $sys)) {
  if (-not (Test-Path $f)) { throw "WinDivert file missing: $f" }
  $sig = Get-Content -AsByteStream -TotalCount 2 -LiteralPath $f
  if (-not ($sig[0] -eq 0x4D -and $sig[1] -eq 0x5A)) { throw "Not a valid executable: $f" }
}
Write-Host 'WinDivert files OK.' -ForegroundColor Green

Write-Host '==> build helper' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
$built = $null
if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
  $bDir = Join-Path $root 'out/native'
  if (Test-Path $bDir) { Remove-Item -Recurse -Force $bDir }
  & cmake -S (Join-Path $root 'src/BlurLink.Net') -B $bDir
  if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
  & cmake --build $bDir --config $Configuration
  if ($LASTEXITCODE -ne 0) { throw 'native build failed' }
  $built = Get-ChildItem -Path $bDir -Recurse -Filter 'blurlink-net.exe' | Select-Object -First 1
} elseif (Get-Command g++.exe -ErrorAction SilentlyContinue) {
  $g = (Get-Command g++.exe).Source
  $inc = Join-Path $root 'src/BlurLink.Net/include'
  $src = Join-Path $root 'src/BlurLink.Net/src'
  # All helper sources (mirrors src/BlurLink.Net/CMakeLists.txt; app.rc stays
  # MSVC-only). Globbed so a new source cannot rot a hardcoded list again.
  $files = Get-ChildItem -Path (Join-Path $src '*.cpp') | ForEach-Object { $_.FullName }
  $tmp = Join-Path $root 'out/helper-tmp/blurlink-net.exe'
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $tmp) | Out-Null
  # -static: zero non-system dependencies (no libgcc/libstdc++/winpthread DLLs
  # needed on clean machines — the #1 portable killer).
  & $g -std=c++20 -O2 -static -Wall -Wextra -Werror -I $inc $files -liphlpapi -lws2_32 -o $tmp
  if ($LASTEXITCODE -ne 0) { throw 'helper link failed' }
  $built = Get-Item $tmp
} else {
  throw 'No C++ toolchain found (need MSVC cl.exe or MinGW g++).'
}
Copy-Item -Force $built.FullName (Join-Path $nativeDir 'blurlink-net.exe')
Copy-Item -Force $dll, $sys $nativeDir
Write-Host 'Staged for embedding: helper + WinDivert.dll + WinDivert64.sys' -ForegroundColor Green

Write-Host '==> publish portable single exe' -ForegroundColor Cyan
$pub = Join-Path $root 'out/publish-portable'
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
if ($Full) {
  & dotnet publish (Join-Path $root 'src/BlurLink.Shell/BlurLink.Shell.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $pub --nologo
  if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
  $dist = Join-Path $root 'dist/BlurLink-Full'
} else {
  & dotnet publish (Join-Path $root 'src/BlurLink.Shell/BlurLink.Shell.csproj') `
    -c $Configuration -r win-x64 --self-contained false `
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $pub --nologo
  if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
  $dist = Join-Path $root 'dist/BlurLink'
  Write-Host 'Slim exe: needs the .NET 10 Desktop Runtime (https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe).' -ForegroundColor Yellow
}
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -Force (Join-Path $pub 'BlurLink.exe') (Join-Path $dist 'BlurLink.exe')
if ($Sfx -and $Full) {
  $sevenZipPath = (Get-Command 7z.exe -ErrorAction SilentlyContinue)?.Source
  if ([string]::IsNullOrWhiteSpace($sevenZipPath) -and (Test-Path 'C:\Program Files\7-Zip\7z.exe')) {
    $sevenZipPath = 'C:\Program Files\7-Zip\7z.exe'
  }
  $sfxMod = 'C:\Program Files\7-Zip\7z.sfx'
  if (-not [string]::IsNullOrWhiteSpace($sevenZipPath) -and (Test-Path $sfxMod)) {
    $sfxDir = Join-Path $root 'out/sfx'
    if (Test-Path $sfxDir) { Remove-Item -Recurse -Force $sfxDir }
    New-Item -ItemType Directory -Force -Path $sfxDir | Out-Null
    & $sevenZipPath a -t7z -mx9 -ms=on (Join-Path $sfxDir 'app.7z') (Join-Path $dist 'BlurLink.exe') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '7z compress failed' }
    Set-Content -LiteralPath (Join-Path $sfxDir 'config.txt') -Value @'
;!@Install@!UTF-8!
Title="BlurLink"
BeginPrompt="Extract BlurLink to a temporary folder and run it?"
RunProgram="BlurLink.exe"
;!@InstallEnd@!
'@ -Encoding Ascii
    cmd /c copy /b "$sfxMod" + "$sfxDir\config.txt" + "$sfxDir\app.7z" "$root\dist\BlurLink-SFX.exe" | Out-Null
    Write-Host "SFX single exe: $root/dist/BlurLink-SFX.exe" -ForegroundColor Green
  } else {
    Write-Warning '7-Zip not found — skipping SFX (install 7zip.7zip or place 7z.exe on PATH).'
  }
}
Write-Host "Portable exe: $dist/BlurLink.exe" -ForegroundColor Green
Write-Host 'Helper + WinDivert are embedded; first launch stages them to %LocalAppData%\BlurLink\bin\.' -ForegroundColor Yellow
