#Requires -Version 7.0
<#
.SYNOPSIS
  Single-machine end-to-end test of BlurLink's two WinDivert sessions:
  host mode (Phase 1) and the Join-mode bridge (Phase 2).

.DESCRIPTION
  Drives the real helper over its real named pipe and feeds it real packets
  through a real WinDivert handle, using tools/hostsim to synthesize the other
  end of the wire (see that file for why a loopback harness cannot work).

  Every stage is reported separately, so a partial result is still informative.

  Phase 1 - host mode (see `-Phase host`):
    1. start         host mode comes up and reports its own filter
    2. introduce     the player appears in the roster from its announcement,
                     with the LAN address and Blur port it declared
    3. rebuild       the filter is rebuilt around the new player
    4. prerequisite  does host mode HEAR that player's forward?  (the question
                     the whole feature hinges on -- `forwards heard`)
    5. forward-reply a reply to that player's (LAN, port) is cloned out, the
                     original is still reinjected, and the clone is logged
                     against the player's overlay address
    6. unmatched     a reply to the right address but the wrong port is
                     reported instead of forwarded
    7. refuse-bcast  a reply the host's Blur sends to a broadcast address is
                     refused and counted, never forwarded
    8. stop-clean    stop clears host mode and leaves no helper behind

  Phase 2 - Join-mode bridge (see `-Phase bridge`):
    9. bridge-start  the bridge opens a real handle and reports its own filter
   10. capture       a Blur-shaped LAN discovery broadcast is captured
   11. forward       it is cloned to the host's overlay address, and the
                     original is still reinjected (preserve-original)
   12. announce      the join side INTRODUCES ITSELF to the host
                     (`announcementsSent`) -- BlurLink's own packet, sent only
                     after a real forward has told it where the host will reply
   13. prefix-gate   a broadcast ONE BYTE off the real packet is preserved,
                     never forwarded (the code-side gate, which the filter
                     deliberately cannot do)
   14. echo          a repeat carrying the same IPv4 identity is suppressed by
                     the dedup backstop instead of being cloned a second time
                     (an identity, not "the same bytes": see the stage comment)
   15. rate-limit    a burst of distinct broadcasts is refused by the rate
                     limiter instead of all being forwarded, and every one is
                     still reinjected
   16. exclusive     while that bridge is live, a SECOND helper's `start` is
                     refused as "another BlurLink bridge is already active" --
                     the cross-process lock, with a real handle held
   17. bridge-stop   stop clears the bridge and exits cleanly
   18. release       that second helper's `start` now succeeds: the lock is
                     released on stop, not leaked (proved from the other
                     process, since the same one short-circuits the check)

  Exit codes: 0 = every check passed; 1 = at least one check FAILED;
  2 = COULD NOT RUN (not elevated, Blur is running, or an artifact is missing).
  A 2 is never a pass -- this script needs Administrator because WinDivertOpen
  does, and it refuses to run at a live Blur unless -AllowLiveBlur says so.

  Side effect worth knowing: Phase 2 must inject OUTBOUND broadcasts for the
  bridge to have anything to clone, so packets addressed to
  255.255.255.255:<discoveryPort> DO go out on the local link. Each clone is
  addressed to a TEST-NET-1 (192.0.2.0/24, RFC 5737) overlay address that no
  host owns, so the clones are inert.

  The injected broadcasts are no longer inert in the strictest sense, and that is
  a deliberate change. They now carry the REAL 24-byte discovery payload
  ($RealQueryHex) instead of an 8-byte ASCII stand-in, because a test that uses a
  packet shape the game never sends is not testing the thing that will run. The
  consequence is honest and small: if a real Blur is running and hosting on this
  machine it may treat the injected broadcast as a genuine query and answer it.
  It answers the test's own Blur port ($BlurPort), where nothing listens, so the
  reply goes nowhere -- but it IS real game traffic, so a run alongside a live
  game is no longer strictly inert.

  That cost is now ENFORCED rather than documented: the harness checks for a
  running Blur.exe and refuses to start (exit 2) while one is up, because the
  damage is not one-way. A live game can answer our injected query, and its own
  broadcasts are matched by the very bridge filter some stages count on -- so a
  live game can flip a stage to PASS or FAIL for reasons that have nothing to do
  with BlurLink. Pass -AllowLiveBlur to run anyway; it warns and continues.

.NOTES
  Needs the WinDivert driver and an elevated shell. Run it after
  scripts/test-interop.ps1 (which builds the helper).

  Two elevation traps, both hit while building this:
    * An elevated shell is Windows PowerShell 5.1 by default, which this
      script's #Requires rejects. Invoke pwsh explicitly.
    * On Store/winget installs, `pwsh` on PATH is a WindowsApps execution
      alias that cannot start from an elevated context -- it exits 58 with no
      output at all. Use the real install path (`$PSHOME`).
  Also: the helper's dashboard is only readable once the helper has exited,
  because it holds the log file open -- so failures dump it from the finally
  block rather than reading it mid-run.

  The helper is started with --log-file, and the tail of that log is printed
  when anything fails: the helper's dashboard is the only place that shows how
  far a stop or a start actually got.
#>
[CmdletBinding()]
param(
  [string]$HelperExe = '',
  [string]$SimExe = '',
  [ValidateSet('all', 'host', 'bridge')][string]$Phase = 'all',
  [int]$DiscoveryPort = 50001,
  [string]$HostIp = '',
  [string]$PlayerOverlay = '25.1.2.3',
  [string]$PlayerLan = '10.20.30.40',
  [int]$BlurPort = 51234,
  # A host overlay address that no machine owns (TEST-NET-1, RFC 5737), so the
  # bridge's clones are provably inert -- nothing is sent to a real host.
  [string]$HostOverlay = '192.0.2.1',
  # ...and the overlay address the bridge announces as its own.
  [string]$LocalOverlay = '192.0.2.2',
  # The bridge's payload-prefix gate is code-side, so it is reachable by
  # sending a payload that does or does not start with this prefix. The default
  # is the leading bytes of the REAL captured query ($RealQueryHex), so the gate
  # is proven to ADMIT a genuine Blur packet rather than only a test stand-in.
  [string]$PayloadPrefixHex = '0F0000',
  [int]$PollSeconds = 10,
  [string]$HelperLog = '',
  # Runs the helper with --verbose, so its debug-level reasoning (the echo
  # backstop's exact identity key, for instance) lands in the run log. Off by
  # default: a full packet-level trace is not what a PASS/FAIL summary wants.
  [switch]$HelperVerbose,
  # A live Blur is a hazard to the RESULT, not just noise: the injected
  # broadcasts carry the REAL 24-byte discovery query, so a hosting Blur can
  # answer them, and the game's own broadcasts are matched by the same bridge
  # filter that stages count on. So it is refused by default rather than noted in
  # a comment. This switch is the explicit "I understand -- run anyway": it
  # warns loudly and continues.
  [switch]$AllowLiveBlur
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runHost = ($Phase -eq 'all' -or $Phase -eq 'host')
$runBridge = ($Phase -eq 'all' -or $Phase -eq 'bridge')

function New-TokenHex {
  $b = New-Object byte[] 32
  [System.Security.Cryptography.RandomNumberGenerator]::Fill($b)
  return ([System.BitConverter]::ToString($b)).Replace('-', '')
}

function New-PipeName {
  $b = [byte[]](1..8 | ForEach-Object { Get-Random -Max 256 })
  return 'BlurLink-' + ([System.BitConverter]::ToString($b)).Replace('-', '')
}

# --- preconditions: fail loudly rather than reporting a false pass ---

# The Blur guard runs FIRST, ahead of the elevation check, for two reasons. It is
# the safety precondition -- this harness injects the REAL 24-byte discovery
# query now, so a hosting Blur can treat one as a genuine query and answer it,
# and the game's own broadcasts are matched by the same bridge filter the bridge
# stages count on (an extra packet flips a stage either way) -- and it is
# answerable WITHOUT an elevated shell, so someone with the game open hears the
# real reason on the first try instead of after an elevation round trip.
$liveBlur = @(Get-Process -Name 'Blur' -ErrorAction SilentlyContinue)
if ($liveBlur.Count -gt 0) {
  $blurPids = ($liveBlur | Sort-Object Id | ForEach-Object { $_.Id }) -join ', '
  if (-not $AllowLiveBlur) {
    Write-Host "CANNOT RUN: Blur is running (PID $blurPids)." -ForegroundColor Yellow
    Write-Host 'This harness injects the REAL 24-byte discovery query, byte for byte.' -ForegroundColor Yellow
    Write-Host 'A hosting Blur can treat an injected broadcast as a genuine query and answer it,' -ForegroundColor Yellow
    Write-Host 'and the game''s own broadcasts are matched by the same bridge filter the bridge' -ForegroundColor Yellow
    Write-Host 'stages count on -- a live game can flip a stage for reasons unrelated to BlurLink.' -ForegroundColor Yellow
    Write-Host 'Close Blur and re-run, or pass -AllowLiveBlur to run anyway.' -ForegroundColor Yellow
    exit 2
  }
  Write-Host "WARNING: Blur is running (PID $blurPids) and -AllowLiveBlur was passed -- continuing." -ForegroundColor Yellow
  Write-Host 'Expect real game traffic in the captures: a stage that counts broadcasts, or asserts a' -ForegroundColor Yellow
  Write-Host 'specific packet count, can flip. A failure here may be the game rather than the code --' -ForegroundColor Yellow
  Write-Host 're-run with Blur closed before believing it.' -ForegroundColor Yellow
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host 'CANNOT RUN: this test needs Administrator (WinDivertOpen requires it).' -ForegroundColor Yellow
  Write-Host 'Start an elevated PowerShell and run this script again.' -ForegroundColor Yellow
  exit 2
}

if ([string]::IsNullOrWhiteSpace($HelperExe)) { $HelperExe = Join-Path $root 'out/interop/blurlink-net.exe' }
if ([string]::IsNullOrWhiteSpace($SimExe)) { $SimExe = Join-Path $root 'out/hostsim/hostsim.exe' }
if ([string]::IsNullOrWhiteSpace($HelperLog)) {
  $HelperLog = Join-Path ([System.IO.Path]::GetTempPath()) 'blurlink-e2e.log'
}

if (-not (Test-Path $SimExe)) {
  Write-Host '==> building hostsim with MinGW g++' -ForegroundColor Cyan
  $g = (Get-Command g++.exe -ErrorAction Stop).Source
  $inc = Join-Path $root 'src/BlurLink.Net/include'
  $net = Join-Path $root 'src/BlurLink.Net/src'
  $outDir = Split-Path -Parent $SimExe
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  # This list must mirror tools/hostsim/CMakeLists.txt. It went stale once
  # already (a missing source only shows up on a from-scratch build, and the
  # script only builds when the exe is absent), so treat the two as one list.
  $files = @((Join-Path $root 'tools/hostsim/hostsim.cpp'),
    (Join-Path $net 'announce.cpp'), (Join-Path $net 'config.cpp'),
    (Join-Path $net 'packet.cpp'), (Join-Path $net 'windivert_api.cpp'))
  & $g -std=c++20 -O2 -static -Wall -Wextra -Werror -I $inc $files -liphlpapi -lws2_32 -o $SimExe
  if ($LASTEXITCODE -ne 0) { throw 'hostsim link failed' }
}

if (-not (Test-Path $HelperExe)) {
  Write-Host "CANNOT RUN: helper not found at $HelperExe" -ForegroundColor Yellow
  Write-Host 'Run scripts/test-interop.ps1 first (it builds the helper), or pass -HelperExe.' -ForegroundColor Yellow
  exit 2
}

# WinDivert needs the DLL *and* the driver .sys together: WinDivertOpen()
# installs the driver on demand from its own directory, so staging only the DLL
# fails with "driver not found" (code 2) on a machine that has never run it.
# Both are staged beside BOTH processes.
$vendored = Join-Path $root 'third-party/WinDivert/x64'
foreach ($needed in @('WinDivert.dll', 'WinDivert64.sys')) {
  if (-not (Test-Path (Join-Path $vendored $needed))) {
    Write-Host "CANNOT RUN: missing third-party/WinDivert/x64/$needed" -ForegroundColor Yellow
    Write-Host 'See third-party/WinDivert/README.md (WinDivert 2.2.2 x64).' -ForegroundColor Yellow
    exit 2
  }
}
foreach ($dir in @((Split-Path -Parent $HelperExe), (Split-Path -Parent $SimExe))) {
  foreach ($needed in @('WinDivert.dll', 'WinDivert64.sys')) {
    $src = Join-Path $vendored $needed
    $dst = Join-Path $dir $needed
    # A loaded WinDivert driver keeps its .sys locked until the service stops
    # or the machine reboots, so overwriting it can fail even when it is the
    # right file already. What matters is only that it is THERE, so a failed
    # copy over an existing file is not an error.
    if (Test-Path $dst) {
      try { Copy-Item -Force $src $dst } catch { }
    } else {
      Copy-Item -Force $src $dst
    }
    if (-not (Test-Path $dst)) {
      Write-Host "CANNOT RUN: could not stage $needed into $dir" -ForegroundColor Yellow
      exit 2
    }
  }
}

# The host's own address is the destination of the packets we inject as inbound
# and the source of those we inject as outbound. It is cosmetic to host mode's
# filter (which scopes ports and the player's LAN address), but it must be a real
# local address so GetBestInterface() can resolve an interface index.
if ([string]::IsNullOrWhiteSpace($HostIp)) {
  $HostIp = [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
    Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
    ForEach-Object { $_.IPAddressToString } |
    Where-Object { $_ -notlike '127.*' } |
    Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($HostIp)) {
  Write-Host 'CANNOT RUN: could not determine this machine''s IPv4 address; pass -HostIp.' -ForegroundColor Yellow
  exit 2
}

# Host mode validates adapterIfIndex but does not put it in the filter, so this
# only has to be a real, non-zero interface. Take the one owning HostIp.
$AdapterIfIndex = 1
try {
  $found = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
    Where-Object { $_.IPAddress -eq $HostIp } | Select-Object -First 1).InterfaceIndex
  if ($found) { $AdapterIfIndex = [int]$found }
} catch { }

# The second player exists to reach the refusal path at all. Host mode's filter
# scopes outbound replies to the accepted players' LAN addresses, so a reply to
# 255.255.255.255 is excluded by the FILTER before the classifier can see it.
# A player whose own LAN address ends in .255 is the one case where a
# broadcast-shaped destination does match the filter -- which is exactly the
# documented limitation in docs/architecture.md, so the refusal is reachable
# and worth proving rather than asserting something that can never happen.
$BcastOverlay = '25.1.2.9'
$BcastLan = ($PlayerLan -replace '\.[0-9]+$', '.255')
$BcastPort = $BlurPort + 100

# The bridge's own broadcast: 255.255.255.255 is the documented destination of
# Blur's LAN discovery, and the bridge's default broadcastDestination. What the
# filter matches on is exactly this destination + port pair.
$BridgeBcast = '255.255.255.255'

# The REAL Blur LAN discovery query, captured byte for byte from a live game on
# 2026-09-12 (one machine, real WinDivert, Blur bound to 0.0.0.0:50001). 24 bytes,
# sent from the discovery port to 255.255.255.255:50001.
#
# The harness injects THIS rather than an ASCII stand-in, so the capture path,
# the payload-prefix gate, the echo backstop and the rate limiter are all
# exercised against the shape the real game actually sends. The stand-in
# (`BLSIMFWD`, the injector's own default) was never a faithful shape -- the real
# payload contains no ASCII text at all. Capture, byte offsets, and the real host
# answer this query produced: docs/packet-research.md.
#
# $RealQueryMutatedHex is NOT a typo for the line above: it differs in exactly ONE
# byte (leading 0x0f -> 0x1f) and exists so the prefix gate can be tested with a
# packet that is otherwise byte-identical to a genuine one. That is a stronger
# negative than an obviously-wrong string, because it proves the gate keys on the
# bytes rather than rejecting anything unfamiliar.
$RealQueryHex = '0f0000000000002c010000008a656eb70adfd31801000000'
$RealQueryMutatedHex = '1f0000000000002c010000008a656eb70adfd31801000000'

function Send-PipeLine {
  param([System.IO.StreamReader]$Reader, [System.IO.StreamWriter]$Writer, [string]$Line)
  $Writer.WriteLine($Line)
  return Read-Line $Reader
}

# A test that hangs is worse than a test that fails: the helper's command loop
# is single-threaded, so a command that never returns also stops its watchdog
# from ever firing. Read with a deadline instead of blocking forever.
function Read-Line {
  param([System.IO.StreamReader]$Reader, [int]$TimeoutMs = 20000)
  $task = $Reader.ReadLineAsync()
  if (-not $task.Wait($TimeoutMs)) {
    Write-Host "  (no reply within ${TimeoutMs}ms -- helper is not responding)" -ForegroundColor Yellow
    return '{"type":"error","message":"harness-timeout"}'
  }
  return $task.Result
}

function Start-Helper {
  param([string]$Pipe, [string]$Token, [int]$Watchdog, [string]$LogPath)
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $HelperExe
  $psi.ArgumentList.Add('--pipe'); $psi.ArgumentList.Add($Pipe)
  $psi.ArgumentList.Add('--token'); $psi.ArgumentList.Add($Token)
  $psi.ArgumentList.Add('--watchdog-sec'); $psi.ArgumentList.Add("$Watchdog")
  # The dashboard is the only record of how far a start/stop actually got.
  $psi.ArgumentList.Add('--log-file'); $psi.ArgumentList.Add($LogPath)
  if ($HelperVerbose) { $psi.ArgumentList.Add('--verbose') }
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  return [System.Diagnostics.Process]::Start($psi)
}

function Watch-HelperOutput {
  param($Proc, $Sink)
  Register-ObjectEvent -InputObject $Proc -EventName OutputDataReceived -Action {
    param($s, $e)
    if ($e.Data -ne $null) { $event.MessageData.Add($e.Data) }
  } -MessageData $Sink | Out-Null
  $Proc.BeginOutputReadLine() | Out-Null
  $Proc.BeginErrorReadLine() | Out-Null
}

function Connect-Pipe {
  param([string]$Pipe, [int]$TimeoutSec = 15)
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    try {
      $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
      $c.Connect(1000)
      $enc = New-Object System.Text.UTF8Encoding($false)
      $r = New-Object System.IO.StreamReader($c, $enc)
      $w = New-Object System.IO.StreamWriter($c, $enc)
      # Without AutoFlush every command sits in the writer's buffer, the helper
      # never sees it, and the read below blocks forever.
      $w.AutoFlush = $true
      return @{ Client = $c; Reader = $r; Writer = $w }
    } catch { Start-Sleep -Milliseconds 300 }
  }
  throw "could not connect to pipe $Pipe"
}

# Starts a helper and completes the hello handshake. Every test helper goes
# through this, so the second one (the exclusivity probe) cannot drift from the
# first -- and a helper whose hello fails is torn down rather than left running.
function Start-HelperSession {
  param([string]$LogPath, $Sink, [int]$Watchdog = 120)
  $pipe = New-PipeName
  $token = New-TokenHex
  $p = Start-Helper -Pipe $pipe -Token $token -Watchdog $Watchdog -LogPath $LogPath
  Watch-HelperOutput -Proc $p -Sink $Sink
  $c = $null
  try {
    $c = Connect-Pipe -Pipe $pipe
    $r = Send-PipeLine $c.Reader $c.Writer (@{ type = 'hello'; token = $token; protocolVersion = 1 } |
      ConvertTo-Json -Compress)
  } catch {
    if (-not $p.HasExited) { try { $p.Kill() } catch { } }
    throw
  }
  return @{ Proc = $p; Conn = $c; Token = $token; Pipe = $pipe; Hello = $r }
}

# Best-effort clean stop: `shutdown` when the pipe still answers, then kill.
function Stop-HelperSession {
  param($Session)
  if ($null -eq $Session) { return }
  if ($null -ne $Session.Conn) {
    try {
      [void](Send-PipeLine $Session.Conn.Reader $Session.Conn.Writer (@{ type = 'shutdown'; token = $Session.Token } |
          ConvertTo-Json -Compress))
    } catch { }
    try { $Session.Conn.Client.Dispose() } catch { }
    $Session.Conn = $null
  }
  Start-Sleep -Milliseconds 300
  if (-not $Session.Proc.HasExited) { try { $Session.Proc.Kill() } catch { } }
}

function Test-HasText($Value, [string]$Needle) {
  return $null -ne $Value -and "$Value".Contains($Needle)
}

$script:failures = 0
$script:skips = 0
function Check([string]$Name, [bool]$Cond, [string]$Detail = '') {
  if ($Cond) { Write-Host "PASS $Name" -ForegroundColor Green }
  else { Write-Host "FAIL $Name $Detail" -ForegroundColor Red; $script:failures++ }
}
function Skip([string]$Name, [string]$Why) {
  Write-Host "SKIP $Name ($Why)" -ForegroundColor Yellow
  $script:skips++
}

# The main helper's command channel, so Get-Status/Wait-For do not have to be
# threaded through every call site.
$script:mainConn = $null
$script:mainOut = New-Object System.Collections.Generic.List[string]
$script:probeOut = New-Object System.Collections.Generic.List[string]

$mainLog = $HelperLog
# A separate log: two helpers writing one file would interleave, and the probe's
# dashboard is what shows whether IT opened a handle or was refused.
$probeLog = "$HelperLog.probe.log"
foreach ($f in @($mainLog, $probeLog)) {
  if (Test-Path $f) { Remove-Item -Force $f -ErrorAction SilentlyContinue }
}
$main = $null
$probe = $null

# Runs the injector; fails the stage if the packet never went out.
function Inject([string]$Label, [string[]]$SimArgs) {
  $out = & $SimExe @SimArgs 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL inject-$Label $out" -ForegroundColor Red
    $script:failures++
    return $false
  }
  Write-Host "  injected: $out" -ForegroundColor DarkGray
  return $true
}

function Get-Status {
  param($Conn = $null)
  if ($null -eq $Conn) { $Conn = $script:mainConn }
  $r = Send-PipeLine $Conn.Reader $Conn.Writer (@{ type = 'get_status'; token = $script:mainToken } |
      ConvertTo-Json -Compress)
  return ($r | ConvertFrom-Json)
}

# Polls get_status until $Predicate returns true, or the deadline passes.
function Wait-For([scriptblock]$Predicate, [int]$Seconds = $PollSeconds) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  $s = $null
  while ((Get-Date) -lt $deadline) {
    $s = Get-Status
    if (& $Predicate $s) { return $s }
    Start-Sleep -Milliseconds 250
  }
  return $s
}

function Show-HelperLog {
  foreach ($f in @($mainLog, $probeLog)) {
    if (Test-Path $f) {
      Write-Host "`n== helper dashboard ($f) ==" -ForegroundColor Cyan
      Get-Content -Path $f -Tail 40 | ForEach-Object { Write-Host "  $_" }
    } else {
      Write-Host "`n(no helper log at $f)" -ForegroundColor Yellow
    }
  }
}

try {
  Write-Host "helper log: $mainLog" -ForegroundColor DarkGray
  $main = Start-HelperSession -LogPath $mainLog -Sink $script:mainOut
  $script:mainConn = $main.Conn
  $script:mainToken = $main.Token
  $conn = $main.Conn
  $proc = $main.Proc
  $token = $main.Token

  Check 'hello-ok' (Test-HasText $main.Hello '"type":"status"') $main.Hello

  if ($runHost) {
    Write-Host "`n== PHASE 1: host mode on $HostIp : $DiscoveryPort (adapter $AdapterIfIndex; player $PlayerOverlay / $PlayerLan`:$BlurPort) ==" -ForegroundColor Cyan

    # --- stage 1: host mode comes up ---
    $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'start_host'; token = $token
        discoveryUdpPort = $DiscoveryPort; adapterIfIndex = $AdapterIfIndex
        rateLimitPerSecond = 10; rateLimitBurst = 20 } | ConvertTo-Json -Compress)
    Check 'start-host-ok' ((Test-HasText $r '"type":"status"') -and (Test-HasText $r '"hostActive":true')) $r

    $s = Get-Status
    Check 'host-filter-is-host-modes-own' (Test-HasText $s.hostFilter '47811') $s.hostFilter
    Check 'host-filter-has-no-players-yet' (-not (Test-HasText $s.hostFilter $PlayerLan)) $s.hostFilter

    # --- stage 2: the player introduces itself ---
    if (Inject 'intro' @('intro', '--player-overlay', $PlayerOverlay, '--player-lan', $PlayerLan,
        '--host-ip', $HostIp, '--blur-port', "$BlurPort")) {
      $s = Wait-For { param($x) @($x.hostPlayers | Where-Object { $_.overlayIp -eq $PlayerOverlay }).Count -ge 1 }
      $p = @($s.hostPlayers | Where-Object { $_.overlayIp -eq $PlayerOverlay })
      Check 'player-appears-in-roster' ($p.Count -eq 1) "$($s.hostPlayers | ConvertTo-Json -Compress)"
      if ($p.Count -eq 1) {
        Check 'roster-keeps-announced-lan' ($p[0].lanIp -eq $PlayerLan) $p[0].lanIp
        Check 'roster-keeps-announced-blur-port' ($p[0].blurSourcePort -eq $BlurPort) $p[0].blurSourcePort
        Check 'roster-entry-is-in-filter' ($p[0].inFilter -eq $true) $p[0].inFilter
      }

      # --- stage 3: the filter is rebuilt around the new player (debounced ~2s) ---
      $s = Wait-For { param($x) $x.hostFilterReopens -ge 1 -and (Test-HasText $x.hostFilter $PlayerLan) }
      Check 'filter-rebuilt-for-the-player' ($s.hostFilterReopens -ge 1) "reopens=$($s.hostFilterReopens)"
      Check 'filter-now-scopes-the-player' (Test-HasText $s.hostFilter $PlayerLan) $s.hostFilter
      # The handle was reopened to change the filter; let it settle before relying
      # on it, because a reply in flight during the reopen is missed by design.
      Start-Sleep -Milliseconds 750

      # --- stage 4: the prerequisite, now that the player is accepted ---
      # ORDER MATTERS, and this is the finding the first elevated run produced:
      # before any player is accepted, host mode's filter is the introduction term
      # alone, so a forward from an unknown source cannot match it and CANNOT be
      # counted. `forwards heard` is therefore only meaningful once a player is in
      # the roster -- which is also why it reads 0 for a friend who has not
      # announced yet, not only for one whose traffic is not arriving.
      # The real captured query is what a player's Blur actually broadcasts, so
      # this is the genuine forward shape arriving at host mode -- not a stand-in.
      if (Inject 'forward' @('forward', '--player-lan', $PlayerLan, '--host-ip', $HostIp,
          '--blur-port', "$BlurPort", '--discovery-port', "$DiscoveryPort",
          '--payload-hex', $RealQueryHex)) {
        $s = Wait-For { param($x) $x.hostForwardsHeard -ge 1 }
        Check 'prerequisite-forward-heard' ($s.hostForwardsHeard -ge 1) "forwardsHeard=$($s.hostForwardsHeard)"
      }
    }

    # --- stage 5: a reply to the player is cloned out, and the original kept ---
    # Baselines measured, not assumed: the counters are cumulative, so "did THIS
    # packet get forwarded" is only answerable relative to where they started.
    $beforeStatus = Get-Status
    $beforeForwarded = [long]$beforeStatus.hostRepliesForwarded
    $beforeReinjected = [long]$beforeStatus.hostReinjected
    if (Inject 'reply' @('reply', '--player-lan', $PlayerLan, '--host-ip', $HostIp,
        '--blur-port', "$BlurPort", '--discovery-port', "$DiscoveryPort")) {
      $s = Wait-For { param($x) $x.hostRepliesForwarded -ge ($beforeForwarded + 1) }
      Check 'reply-forwarded-to-the-player' ($s.hostRepliesForwarded -ge ($beforeForwarded + 1)) `
        "forwarded=$($s.hostRepliesForwarded) (was $beforeForwarded)"
      Check 'original-reply-still-reinjected' ($s.hostReinjected -gt $beforeReinjected) `
        "reinjected=$($s.hostReinjected) (was $beforeReinjected)"
      $dash = ($script:mainOut -join "`n")
      Check 'clone-logged-with-the-overlay-destination' ((Test-HasText $dash '[host-fwd]') -and (Test-HasText $dash $PlayerOverlay)) `
        'missing [host-fwd] line naming the player overlay'
    }

    # --- stage 6: right address, wrong port -> reported, never forwarded ---
    $beforeFwd = (Get-Status).hostRepliesForwarded
    if (Inject 'wrong-port' @('reply', '--player-lan', $PlayerLan, '--host-ip', $HostIp,
        '--blur-port', "$($BlurPort + 1)", '--discovery-port', "$DiscoveryPort")) {
      $s = Wait-For { param($x) $x.hostUnmatchedReplies -ge 1 }
      Check 'wrong-port-reply-reported' ($s.hostUnmatchedReplies -ge 1) "unmatched=$($s.hostUnmatchedReplies)"
      Check 'wrong-port-reply-not-forwarded' ($s.hostRepliesForwarded -eq $beforeFwd) `
        "forwarded went $beforeFwd -> $($s.hostRepliesForwarded)"
    }

    # --- stage 7: a broadcast-shaped reply is refused and counted ---
    # See $BcastLan: a LAN address ending in .255 is the only case where the
    # filter admits a broadcast destination, so it is the only way to reach the
    # classifier's refusal end to end -- and it is the documented limitation.
    if (Inject 'bcast-intro' @('intro', '--player-overlay', $BcastOverlay, '--player-lan', $BcastLan,
        '--host-ip', $HostIp, '--blur-port', "$BcastPort")) {
      $s = Wait-For { param($x) (Test-HasText $x.hostFilter $BcastLan) }
      Check 'dot255-player-joins-the-filter' (Test-HasText $s.hostFilter $BcastLan) $s.hostFilter

      $beforeBcast = [long]$s.hostBroadcastReplies
      $beforeFwd = [long]$s.hostRepliesForwarded
      $beforeReinjected = [long]$s.hostReinjected
      if (Inject 'bcast-reply' @('reply', '--player-lan', $BcastLan, '--host-ip', $HostIp,
          '--blur-port', "$BcastPort", '--discovery-port', "$DiscoveryPort")) {
        $s = Wait-For { param($x) $x.hostBroadcastReplies -ge ($beforeBcast + 1) }
        Check 'broadcast-reply-refused' ($s.hostBroadcastReplies -ge ($beforeBcast + 1)) `
          "broadcast=$($s.hostBroadcastReplies) (was $beforeBcast)"
        Check 'broadcast-reply-not-forwarded' ($s.hostRepliesForwarded -eq $beforeFwd) `
          "forwarded went $beforeFwd -> $($s.hostRepliesForwarded)"
        Check 'refused-reply-still-reinjected' ($s.hostReinjected -gt $beforeReinjected) `
          "reinjected=$($s.hostReinjected) (was $beforeReinjected)"
      }

      # The limited broadcast address never matches the filter at all -- the only
      # destinations it admits are the accepted players' LAN addresses -- so host
      # mode never even sees this one. Asserted so the difference between "the
      # filter excluded it" and "the classifier refused it" is recorded rather
      # than assumed. The explicit address matters: the default for `broadcast` is
      # the player's subnet broadcast, which IS in the filter here because the
      # dot-255 player joined it.
      $beforeFwd = (Get-Status).hostRepliesForwarded
      if (Inject 'offfilter-broadcast' @('broadcast', '--player-lan', $PlayerLan, '--host-ip', $HostIp,
          '--blur-port', "$BlurPort", '--discovery-port', "$DiscoveryPort",
          '--broadcast-ip', '255.255.255.255')) {
        Start-Sleep -Milliseconds 750
        $s = Get-Status
        Check 'offfilter-broadcast-never-forwarded' ($s.hostRepliesForwarded -eq $beforeFwd) `
          "forwarded went $beforeFwd -> $($s.hostRepliesForwarded)"
      }
    }

    # --- stage 8: stop is clean ---
    $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'stop'; token = $token } | ConvertTo-Json -Compress)
    $s = $r | ConvertFrom-Json
    Check 'stop-clears-host-mode' ($s.type -eq 'status' -and $s.hostActive -eq $false) $r
  }

  if ($runBridge) {
    Write-Host "`n== PHASE 2: Join-mode bridge, cloning $BridgeBcast`:$DiscoveryPort to $HostOverlay (announcing as $LocalOverlay) ==" -ForegroundColor Cyan

    # The token is per-helper, so the body has to be built per-helper. Sending
    # the main helper's token down the probe's pipe is rejected as
    # "unauthorized", which would make the exclusivity check pass for a reason
    # that has nothing to do with the lock.
    function New-BridgeStartBody([string]$ForToken) {
      # rate 2/s, burst 2 is deliberately small: stage 15 needs the limiter to
      # actually refuse something, and a generous rate would never reach it.
      return @{
        type = 'start'; token = $ForToken; hostOverlayIp = $HostOverlay
        discoveryUdpPort = $DiscoveryPort; broadcastDestination = $BridgeBcast
        payloadPrefixHex = $PayloadPrefixHex; preserveOriginalBroadcast = $true
        localOverlayIp = $LocalOverlay; announceToHost = $true
        rateLimitPerSecond = 2; rateLimitBurst = 2; adapterIfIndex = $AdapterIfIndex
      } | ConvertTo-Json -Compress
    }

    # --- stage 9: the bridge opens a real handle ---
    $bridgeStart = New-BridgeStartBody $token
    $r = Send-PipeLine $conn.Reader $conn.Writer $bridgeStart
    Check 'bridge-start-ok' ((Test-HasText $r '"type":"status"') -and (Test-HasText $r '"active":true')) $r

    $s = Get-Status
    Check 'bridge-reports-its-own-filter' ((Test-HasText $s.filter 'outbound') -and
      (Test-HasText $s.filter "$DiscoveryPort") -and (Test-HasText $s.filter $BridgeBcast)) $s.filter

    # --- stage 10/11: a Blur-shaped broadcast is captured and cloned out ---
    $before = Get-Status
    $beforeCaptured = [long]$before.captured
    $beforeFwd = [long]$before.forwarded
    $beforeReinjected = [long]$before.reinjected
    $injectedBlur = Inject 'blur-broadcast' @('blur-broadcast', '--blur-lan', $HostIp,
      '--blur-port', "$BlurPort", '--discovery-port', "$DiscoveryPort",
      '--broadcast-ip', $BridgeBcast, '--payload-hex', $RealQueryHex)
    if ($injectedBlur) {
      $s = Wait-For { param($x) $x.forwarded -ge ($beforeFwd + 1) }
      Check 'bridge-captures-blur-broadcast' ($s.captured -ge ($beforeCaptured + 1)) `
        "captured=$($s.captured) (was $beforeCaptured)"
      Check 'bridge-forwards-to-host-overlay' ($s.forwarded -ge ($beforeFwd + 1)) `
        "forwarded=$($s.forwarded) (was $beforeFwd)"
      Check 'bridge-original-still-reinjected' ($s.reinjected -gt $beforeReinjected) `
        "reinjected=$($s.reinjected) (was $beforeReinjected)"
      $dash = ($script:mainOut -join "`n")
      Check 'bridge-forward-logged' ((Test-HasText $dash '[fwd]') -and (Test-HasText $dash $HostOverlay)) `
        'missing [fwd] line naming the host overlay'

      # --- stage 12: the join side introduces itself ---
      # BlurLink's own packet (never a Blur constant), and only after a real
      # forward told the bridge which (LAN, port) the host will reply to. This is
      # the join half of host mode's roster, and this is its only runtime proof.
      $s = Wait-For { param($x) $x.announcementsSent -ge 1 }
      Check 'bridge-announces-itself-to-the-host' ($s.announcementsSent -ge 1) `
        "announcementsSent=$($s.announcementsSent)"
    }

    # --- stage 13: the code-side payload-prefix gate ---
    # The filter is port+address only on purpose, so this gate is the only thing
    # standing between a same-shaped broadcast from another app and a clone. A
    # payload that does not carry the prefix must be preserved, not forwarded.
    # The negative here is the REAL packet with a single byte flipped, so the gate
    # is shown refusing something indistinguishable from a genuine query except
    # for that byte -- not merely refusing an unfamiliar string.
    $before = Get-Status
    $beforeFwd = [long]$before.forwarded
    $beforeReinjected = [long]$before.reinjected
    if (Inject 'wrong-payload' @('blur-broadcast', '--blur-lan', $HostIp,
        '--blur-port', "$BlurPort", '--discovery-port', "$DiscoveryPort",
        '--broadcast-ip', $BridgeBcast, '--payload-hex', $RealQueryMutatedHex)) {
      Start-Sleep -Milliseconds 750
      $s = Get-Status
      Check 'bridge-payload-prefix-gate-holds' ($s.forwarded -eq $beforeFwd) `
        "forwarded went $beforeFwd -> $($s.forwarded)"
      Check 'rejected-payload-still-reinjected' ($s.reinjected -gt $beforeReinjected) `
        "reinjected=$($s.reinjected) (was $beforeReinjected)"
    }

    # --- stage 14: the echo backstop suppresses a repeat ---
    # Two broadcasts with the SAME explicit IPv4 ID: the first is forwarded, the
    # second is an echo of our own reinjection and must be suppressed.
    #
    # --ip-id is load-bearing, and finding out why was worth the run: the first
    # version of this stage just sent the same bytes twice, and the bridge
    # forwarded BOTH -- correctly. An IPv4 ID of 0 is not a value but "assign me
    # one", so Windows gave each injected packet a consecutive ID (45133, 45134
    # were the observed values) and the identity check, which keys on the ID, was
    # right to call them two distinct packets. A packet whose ID the injector
    # pins IS delivered verbatim, which is what makes a genuine echo testable.
    $before = Get-Status
    $beforeFwd = [long]$before.forwarded
    $beforeDedup = [long]$before.dedupSkipped
    $beforeReinjected = [long]$before.reinjected
    if (Inject 'repeat-identical' @('blur-broadcast', '--blur-lan', $HostIp,
        '--blur-port', "$BlurPort", '--discovery-port', "$DiscoveryPort",
        '--broadcast-ip', $BridgeBcast, '--payload-hex', $RealQueryHex,
        '--ip-id', '4242', '--count', '2')) {
      $s = Wait-For { param($x) $x.dedupSkipped -ge ($beforeDedup + 1) }
      Check 'bridge-echo-backstop-suppresses-repeat' ($s.dedupSkipped -ge ($beforeDedup + 1)) `
        "dedupSkipped=$($s.dedupSkipped) (was $beforeDedup)"
      # The echo being DETECTED is the assertion: dedup runs before the limiter,
      # so a rising dedupSkipped proves the repeat never reached it -- and that
      # the backstop ordered the two checks the way the design documents.
      Check 'repeat-not-cloned-twice' ($s.forwarded -lt ($beforeFwd + 2)) `
        "forwarded went $beforeFwd -> $($s.forwarded) for 2 same-identity broadcasts"
      Check 'suppressed-repeat-still-reinjected' ($s.reinjected -ge ($beforeReinjected + 2)) `
        "reinjected=$($s.reinjected) (was $beforeReinjected)"
    }

    # --- stage 15: the rate limiter refuses instead of hot-forwarding ---
    # --vary-id is load-bearing here, and --ip-id keeps it deterministic: six
    # DISTINCT identities, or the dedup backstop (correctly) suppresses the
    # repeats and the limiter is never reached at all. Asserted loosely on
    # purpose -- the limiter refills with wall-clock time, so the exact count is
    # not stable, but "some were refused" is.
    $before = Get-Status
    $beforeFwd = [long]$before.forwarded
    $beforeDropped = [long]$before.dropped
    $beforeReinjected = [long]$before.reinjected
    if (Inject 'rate-burst' @('blur-broadcast', '--blur-lan', $HostIp,
        '--blur-port', "$BlurPort", '--discovery-port', "$DiscoveryPort",
        '--broadcast-ip', $BridgeBcast, '--payload-hex', $RealQueryHex,
        '--ip-id', '100', '--count', '6', '--vary-id')) {
      $s = Wait-For { param($x) $x.dropped -ge ($beforeDropped + 1) }
      Check 'bridge-rate-limit-refuses' ($s.dropped -ge ($beforeDropped + 1)) `
        "dropped=$($s.dropped) (was $beforeDropped)"
      Check 'bridge-rate-limit-not-everything-forwarded' ($s.forwarded -lt ($beforeFwd + 6)) `
        "forwarded=$($s.forwarded) (was $beforeFwd) for 6 distinct broadcasts"
      Check 'rate-limited-still-reinjected' ($s.reinjected -ge ($beforeReinjected + 6)) `
        "reinjected=$($s.reinjected) (was $beforeReinjected)"
    }

    # --- stage 16: the cross-process lock, with a real handle held ---
    # scripts/test-interop.ps1 already covers this lock's logic with a locally
    # created event; what it cannot prove is that a LIVE bridge holds it. This
    # does, so "another BlurLink bridge is already active" is verified against the
    # condition that actually produces it.
    $probe = Start-HelperSession -LogPath $probeLog -Sink $script:probeOut
    Check 'probe-hello-ok' (Test-HasText $probe.Hello '"type":"status"') $probe.Hello
    $probeStart = New-BridgeStartBody $probe.Token
    $r = Send-PipeLine $probe.Conn.Reader $probe.Conn.Writer $probeStart
    Check 'second-helper-refused-while-bridge-live' (Test-HasText $r 'already active') $r

    # --- stage 18: stop clears the bridge ---
    $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'stop'; token = $token } | ConvertTo-Json -Compress)
    $s = $r | ConvertFrom-Json
    Check 'bridge-stop-clears-mode' ($s.type -eq 'status' -and $s.active -eq $false) $r

    # --- stage 17: ...and released the session lock ---
    # The proof is the OTHER process, not this one: the same process short-
    # circuits the lock check, so only a second helper can show it was freed.
    $r = Send-PipeLine $probe.Conn.Reader $probe.Conn.Writer $probeStart
    Check 'session-released-after-bridge-stop' ((Test-HasText $r '"type":"status"') -and
      (Test-HasText $r '"active":true')) $r
    [void](Send-PipeLine $probe.Conn.Reader $probe.Conn.Writer (@{ type = 'stop'; token = $probe.Token } |
        ConvertTo-Json -Compress))
    Stop-HelperSession $probe
    $probe = $null
    Start-Sleep -Milliseconds 500
  }

  # --- final: shutdown, and leave nothing behind ---
  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'shutdown'; token = $token } | ConvertTo-Json -Compress)
  try { $conn.Client.Dispose() } catch { }
  $script:mainConn = $null
  $conn = $null
  $exited = $proc.WaitForExit(10000)
  Check 'helper-exits-on-shutdown' ($exited -and $proc.ExitCode -eq 0) "exit=$($proc.ExitCode)"
  Start-Sleep -Milliseconds 750
  $left = @(Get-Process -Name 'blurlink-net' -ErrorAction SilentlyContinue)
  Check 'no-helper-left-behind' ($left.Count -eq 0) "$($left.Count) still running"
} catch {
  # Any harness-level failure becomes a FAIL with cleanup, never a hang or a
  # stack trace that hides which stage was running.
  Write-Host "FAIL harness-error $($_.Exception.Message)" -ForegroundColor Red
  $script:failures++
} finally {
  if ($null -ne $script:mainConn) { try { $script:mainConn.Client.Dispose() } catch { } }
  Stop-HelperSession $probe
  if ($null -ne $main -and -not $main.Proc.HasExited) { try { $main.Proc.Kill() } catch { } }
  Get-Job -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) {
  Show-HelperLog
  throw "$($script:failures) end-to-end check(s) FAILED"
}
Write-Host "All end-to-end checks passed ($($script:skips) skipped)." -ForegroundColor Green
