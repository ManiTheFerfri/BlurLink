#Requires -Version 7.0
<#
.SYNOPSIS
  Runs all BlurLink tests: managed (dotnet test) + native (CMake/ctest).
#>
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host "==> dotnet test" -ForegroundColor Cyan
& dotnet test (Join-Path $root 'BlurLink.sln') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }

Write-Host "==> docs checks" -ForegroundColor Cyan
& (Join-Path $root 'scripts/check-docs-privacy.ps1')
if ($LASTEXITCODE -ne 0) { throw "privacy check failed" }
& (Join-Path $root 'scripts/check-doc-claims.ps1')
if ($LASTEXITCODE -ne 0) { throw "docs claim check failed" }

if (Get-Command cmake -ErrorAction SilentlyContinue) {
  Write-Host "==> native tests" -ForegroundColor Cyan
  $tDir = Join-Path $root 'tests/BlurLink.Net.Tests'
  $bDir = Join-Path $root 'out/native-tests'
  if (Test-Path $bDir) { Remove-Item -Recurse -Force $bDir } # avoid generator conflicts
  # No MSVC here (dev box without VS)? The bundled Ninja is too old for
  # CMake's C++20 scanning rules — use MinGW Makefiles instead.
  $genArgs = @()
  if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { $genArgs += '-G'; $genArgs += 'MinGW Makefiles' }
  & cmake -S $tDir -B $bDir -DCMAKE_BUILD_TYPE=$Configuration @genArgs
  if ($LASTEXITCODE -ne 0) { throw "native test configure failed" }
  & cmake --build $bDir --config $Configuration
  if ($LASTEXITCODE -ne 0) { throw "native test build failed" }
  & ctest --test-dir $bDir --output-on-failure
  if ($LASTEXITCODE -ne 0) { throw "native tests failed" }
} else {
  Write-Warning "cmake not found — managed tests done; native tests need CMake + a C++ toolchain."
}

Write-Host "All tests passed." -ForegroundColor Green

if (Get-Command g++.exe -ErrorAction SilentlyContinue) {
  Write-Host "==> helper live interop (local pipe, no admin/driver needed)" -ForegroundColor Cyan
  & (Join-Path $root 'scripts/test-interop.ps1')
  if ($LASTEXITCODE -ne 0) { throw "interop checks failed" }
} else {
  Write-Warning "g++ not found — skipping scripts/test-interop.ps1 (CI runs it against the MSVC build)."
}
