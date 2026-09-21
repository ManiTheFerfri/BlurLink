#Requires -Version 7.0
<#
.SYNOPSIS
  Fails when a withdrawn claim is asserted outside its retraction record, or when
  a documented constant no longer matches the code that defines it.
  See docs/claims-registry.json.
#>
[CmdletBinding()]
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$registry = Get-Content -Raw (Join-Path $Root 'docs/claims-registry.json') | ConvertFrom-Json
$extensions = @('.md')
$skipDirs   = @('out', 'dist', 'bin', 'obj', '.git', 'third-party', '.superpowers')
$violations = @()

foreach ($entry in $registry.retractedClaims) {
  $allowed = @($entry.allowedIn)
  foreach ($file in Get-ChildItem -Path $Root -Recurse -File |
      Where-Object { $extensions -contains $_.Extension }) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
    $relSlash = $relative.Replace('\', '/')
    if ($skipDirs | Where-Object { $relSlash -eq $_ -or $relSlash.StartsWith("$_/") -or $relSlash -like "*/$_/*" -or $relative -like "$_*" -or $relative -like "*\$_\*" }) { continue }
    if ($allowed -contains $relSlash) { continue }
    $lineNo = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
      $lineNo++
      if ($line.ToLowerInvariant().Contains($entry.claim.ToLowerInvariant())) {
        $violations += "$relSlash`:$lineNo`: asserts retracted claim '$($entry.id)'"
      }
    }
  }
}

foreach ($constant in $registry.documentedConstants) {
  $code = Get-Content -Raw (Join-Path $Root $constant.inCode)
  if (-not $code.Contains($constant.value)) {
    $violations += "$($constant.inCode): no longer contains '$($constant.value)' for $($constant.name)"
  }
  foreach ($doc in @($constant.mustAppearIn)) {
    $text = Get-Content -Raw (Join-Path $Root $doc)
    if (-not $text.Contains($constant.value)) {
      $violations += "${doc}: does not mention $($constant.value) for $($constant.name)"
    }
  }
}

if ($violations) { $violations; exit 1 }
Write-Host 'claims-ok'
exit 0
