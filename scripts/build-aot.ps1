#Requires -Version 7.0
<#
.SYNOPSIS
  Publishes a NativeAOT single-file build for the size/startup experiment.
  This does NOT change the default deliverable: slim framework-dependent stays
  the shipping flavor until the spike says otherwise (docs/aot-spike.md).
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'dist/BlurLink-Aot'

& dotnet publish (Join-Path $root 'src/BlurLink.Desktop') `
  -c $Configuration -r win-x64 -o $outDir `
  -p:PublishAot=true -p:StripSymbols=true -p:InvariantGlobalization=false
if ($LASTEXITCODE -ne 0) { throw 'AOT publish failed' }

$exe = Join-Path $outDir 'BlurLink.exe'
if (-not (Test-Path $exe)) { throw "AOT publish produced no exe at $exe" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 2)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exe -PassThru
# Cold start = until the process has a window; 10s ceiling so a hang is visible.
$deadline = (Get-Date).AddSeconds(10)
while (-not $proc.HasExited -and $proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 25
  $proc.Refresh()
}
$sw.Stop()
Write-Host ("AOT size: {0} MB, window in {1} ms" -f $sizeMb, $sw.ElapsedMilliseconds)
if (-not $proc.HasExited) { $proc.Kill() }
