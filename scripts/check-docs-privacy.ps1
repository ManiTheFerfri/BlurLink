#Requires -Version 7.0
<#
.SYNOPSIS
  Fails when a scrubbed identifier reappears in the repository.
  See docs/privacy-scrub.md. Structure and offsets are preserved by the scrub,
  so only identifiers are banned (exact literals, not prefixes — prefixes
  collide with synthetic test fixtures sharing those octets).
#>
[CmdletBinding()]
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'

# literal -> why it must not return (docs/privacy-scrub.md).
# Exact author identifiers only. The operator first name is handled separately
# below with a word-boundary match so Manifest/GetManifestResourceNames never match.
$banned = [ordered]@{
  '10.88.14.114' = 'author overlay address'
  '10.88.14.200' = 'author overlay address'
  '10.88.14.255' = 'author overlay broadcast'
  '192.168.1.116' = 'author physical LAN address'
  '192.168.1.200' = 'author physical LAN address'
  'PacketRaft'   = 'author overlay network name'
  '4d0061006e0069' = 'author host name inside captured payload hex'
  'c0a80174'     = 'author physical LAN address inside captured payload hex'
  '0a580e72'     = 'author overlay address inside captured payload hex'
  '15832'        = 'author game process id'
}

$extensions = @('.md', '.ps1', '.cpp', '.h', '.json', '.cs', '.xaml', '.csproj')
$skipDirs   = @('out', 'dist', 'bin', 'obj', '.git', 'third-party', '.superpowers')

$violations = foreach ($file in Get-ChildItem -Path $Root -Recurse -File |
    Where-Object { $extensions -contains $_.Extension }) {
  $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
  $relSlash = $relative.Replace('\', '/')
  if ($skipDirs | Where-Object { $relSlash -eq $_ -or $relSlash.StartsWith("$_/") -or $relSlash -like "*/$_/*" -or $relative -like "$_*" -or $relative -like "*\$_\*" }) { continue }
  if ($relSlash -eq 'docs/privacy-scrub.md' -or $relSlash -eq 'scripts/check-docs-privacy.ps1') { continue }
  $lineNo = 0
  foreach ($line in Get-Content -LiteralPath $file.FullName) {
    $lineNo++
    $lineLower = $line.ToLowerInvariant()
    foreach ($literal in $banned.Keys) {
      $isHex = $literal -cmatch '\A[0-9a-f]+\Z'
      $hit = if ($isHex) { $lineLower.Contains($literal) } else { $line.Contains($literal) }
      if ($hit) {
        "$relSlash`:$lineNo`: $literal ($($banned[$literal]))"
      }
    }
    if ($line -cmatch '\bMani\b') {
      "$relSlash`:$lineNo`: Mani (author machine name)"
    }
  }
}

if ($violations) { $violations; exit 1 }
Write-Host 'privacy-ok'
exit 0
