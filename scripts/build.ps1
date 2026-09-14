#Requires -Version 7.0
<#
.SYNOPSIS
  Builds BlurLink (managed solution + native helper).
.DESCRIPTION
  1. dotnet build BlurLink.sln (x64, Release by default)
  2. CMake build of src/BlurLink.Net (requires MSVC v143 / VS2022)
  3. Copies blurlink-net.exe next to the Desktop output.
#>
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host "==> dotnet build ($Configuration|x64)" -ForegroundColor Cyan
& dotnet build (Join-Path $root 'BlurLink.sln') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Write-Host "==> native helper (CMake + MSVC)" -ForegroundColor Cyan
$netDir = Join-Path $root 'src/BlurLink.Net'
$buildDir = Join-Path $root 'out/native'
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir } # avoid generator conflicts
if (Get-Command cmake -ErrorAction SilentlyContinue) {
  $genArgs = @()
  if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { $genArgs += '-G'; $genArgs += 'MinGW Makefiles' }
  & cmake -S $netDir -B $buildDir -DCMAKE_BUILD_TYPE=$Configuration @genArgs
  if ($LASTEXITCODE -ne 0) { throw "cmake configure failed (install VS2022 + CMake)" }
  & cmake --build $buildDir --config $Configuration
  if ($LASTEXITCODE -ne 0) { throw "native build failed" }

  $helper = Get-ChildItem -Path $buildDir -Recurse -Filter 'blurlink-net.exe' | Select-Object -First 1
  $dest = Join-Path $root "out/$Configuration-x64/net"
  if ($helper) {
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Force $helper.FullName (Join-Path $dest 'blurlink-net.exe')
    Write-Host "Helper staged at $dest/blurlink-net.exe" -ForegroundColor Green
  }
} else {
  Write-Warning "cmake not found — managed build done; build src/BlurLink.Net/blurlink-net.vcxproj in VS2022 manually."
}

Write-Host "Build complete." -ForegroundColor Green
