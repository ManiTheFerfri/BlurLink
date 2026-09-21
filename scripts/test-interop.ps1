#Requires -Version 7.0
<#
.SYNOPSIS
  Live interop test for blurlink-net.exe — no admin, driver, or Blur needed.
.DESCRIPTION
  Builds the helper with the available toolchain (or uses -HelperExe),
  then drives it over its named pipe:
    1. get_status before start (inactive, empty filter)
    2. bad token rejected (unauthorized, nothing happens)
    3. unknown command rejected
    4. start with valid params (expects a clean DRIVER error, not a crash,
       since WinDivert isn't installed for this test)
    5. start with invalid params (expects a validation error)
    6. shutdown (helper exits, exit code 0)
    7. watchdog: fresh helper, one get_status, disconnect, must exit by itself
#>
[CmdletBinding()]
param(
  [string]$HelperExe = '',
  [int]$WatchdogSec = 4
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function New-TokenHex {
  $b = New-Object byte[] 32
  [System.Security.Cryptography.RandomNumberGenerator]::Fill($b)
  return ([System.BitConverter]::ToString($b)).Replace('-', '')
}

if ([string]::IsNullOrWhiteSpace($HelperExe)) {
  $HelperExe = Join-Path $root 'out/interop/blurlink-net.exe'
}
if (-not (Test-Path $HelperExe)) {
  Write-Host '==> building helper with MinGW g++ (link check)' -ForegroundColor Cyan
  $g = (Get-Command g++.exe -ErrorAction Stop).Source
  $inc = Join-Path $root 'src/BlurLink.Net/include'
  $src = Join-Path $root 'src/BlurLink.Net/src'
  $outDir = Split-Path -Parent $HelperExe
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  # Kept in step with src/BlurLink.Net/CMakeLists.txt's blurlink-net sources:
  # a missing file here is a link error, not a silent skip.
  $files = @('main.cpp', 'announce.cpp', 'config.cpp', 'packet.cpp', 'json_min.cpp',
    'windivert_api.cpp', 'bridge.cpp', 'host_classify.cpp', 'host_roster.cpp',
    'host_engine.cpp', 'host_session.cpp', 'ipc_server.cpp') |
    ForEach-Object { Join-Path $src $_ }
  # NOTE: linked WITHOUT app.manifest — MinGW merges its own default manifest
  # and duplicates conflict. The helper doesn't need it (RtlGetVersion is
  # manifest-proof); MSVC/CMake builds embed it (see CMakeLists).
  & $g -std=c++20 -O2 -static -Wall -Wextra -Werror -I $inc $files -liphlpapi -lws2_32 -o $HelperExe
  if ($LASTEXITCODE -ne 0) { throw 'helper link failed' }
}
# Stage the real WinDivert.dll next to the helper when available: exercises
# the true LoadLibrary/GetProcAddress/WinDivertOpen path (unelevated, so the
# driver install is refused with a clean error instead of a crash).
$realDll = Join-Path $root 'third-party/WinDivert/x64/WinDivert.dll'
if ((Test-Path $realDll) -and $HelperExe) {
  Copy-Item -Force $realDll (Join-Path (Split-Path -Parent $HelperExe) 'WinDivert.dll')
}

function Send-PipeLine {
  param([System.IO.StreamReader]$Reader, [System.IO.StreamWriter]$Writer, [string]$Line)
  $Writer.WriteLine($Line)
  return $Reader.ReadLine()
}

function Start-Helper {
  param([string]$Pipe, [string]$Token, [int]$Watchdog, [string]$LogPath = '')
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $HelperExe
  $psi.ArgumentList.Add('--pipe'); $psi.ArgumentList.Add($Pipe)
  $psi.ArgumentList.Add('--token'); $psi.ArgumentList.Add($Token)
  $psi.ArgumentList.Add('--watchdog-sec'); $psi.ArgumentList.Add("$Watchdog")
  if ($LogPath) { $psi.ArgumentList.Add('--log-file'); $psi.ArgumentList.Add($LogPath) }
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $proc = [System.Diagnostics.Process]::Start($psi)
  return $proc
}

$script:helperStdout = New-Object System.Collections.Generic.List[string]
function Watch-HelperOutput {
  param($Proc)
  # Register first, then begin: the dashboard logs from startup, and an
  # unread redirected pipe would block the helper once its buffer fills.
  Register-ObjectEvent -InputObject $Proc -EventName OutputDataReceived -Action {
    param($s, $e)
    if ($e.Data -ne $null) { $event.MessageData.Add($e.Data) }
  } -MessageData $script:helperStdout | Out-Null
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
      $w.AutoFlush = $true
      return @{ Client = $c; Reader = $r; Writer = $w }
    } catch { Start-Sleep -Milliseconds 300 }
  }
  throw "could not connect to pipe $Pipe"
}

$failures = 0
function Check([string]$Name, [bool]$Cond, [string]$Detail = '') {
  if ($Cond) { Write-Host "PASS $Name" -ForegroundColor Green }
  else { Write-Host "FAIL $Name $Detail" -ForegroundColor Red; $script:failures++ }
}

# --- main scenario ---
$pipe = 'BlurLink-' + ([System.BitConverter]::ToString((1..8 | ForEach-Object { Get-Random -Max 256 }) -as [byte[]])).Replace('-', '')
$token = New-TokenHex
$helperLog = Join-Path ([System.IO.Path]::GetTempPath()) ("blurlink-interop-" + [guid]::NewGuid().ToString('N') + ".log")
$proc = Start-Helper -Pipe $pipe -Token $token -Watchdog 60 -LogPath $helperLog
Watch-HelperOutput $proc
try {
  $conn = Connect-Pipe -Pipe $pipe

  # Protocol handshake: commands are refused until a matching hello.
  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'get_status'; token = $token } | ConvertTo-Json -Compress)
  Check 'command-before-hello-rejected' ($r.Contains('send hello')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'hello'; token = $token } | ConvertTo-Json -Compress)
  Check 'hello-missing-version-rejected' ($r.Contains('protocolVersion')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'hello'; token = $token; protocolVersion = 999 } | ConvertTo-Json -Compress)
  Check 'hello-version-mismatch-rejected' ($r.Contains('protocol version mismatch')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'hello'; token = $token; protocolVersion = 1 } | ConvertTo-Json -Compress)
  Check 'hello-ok' ($r.Contains('"type":"status"')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'get_status'; token = $token } | ConvertTo-Json -Compress)
  $j = $r | ConvertFrom-Json
  Check 'status-inactive-before-start' ($j.type -eq 'status' -and $j.active -eq $false) $r
  Check 'watchdog-reported' ($j.watchdogSec -eq 60) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'get_status'; token = ('0' * 64) } | ConvertTo-Json -Compress)
  Check 'bad-token-rejected' ($r.Contains('unauthorized')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'frobnicate'; token = $token } | ConvertTo-Json -Compress)
  Check 'unknown-command-rejected' ($r.Contains('unknown command')) $r

  $start = @{
    type = 'start'; token = $token; hostOverlayIp = '100.96.47.177'
    discoveryUdpPort = 12345; broadcastDestination = '255.255.255.255'
    payloadPrefixHex = ''; preserveOriginalBroadcast = $true
    rateLimitPerSecond = 10; rateLimitBurst = 20; adapterIfIndex = 0
  } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $start
  $dllNextToHelper = Join-Path (Split-Path -Parent $HelperExe) 'WinDivert.dll'
  if (Test-Path $dllNextToHelper) {
    # Real WinDivert.dll present: the loader must get PAST 'not found' into
    # WinDivertOpen, which fails cleanly unelevated (driver install refused).
    # Any coherent error here proves LoadLibrary + all 7 GetProcAddress
    # resolutions + the call itself survived (a broken signature would crash).
    $okErr = $r.Contains('"type":"error"') -and -not $r.Contains('not found')
    Check 'start-with-real-dll-clean-error' $okErr $r
  } else {
    Check 'start-without-driver-clean-error' ($r.Contains('"type":"error"') -and $r.Contains('WinDivert')) $r
  }

  # A failed start must release the bridge-session exclusivity lock, otherwise
  # the next legitimate attempt would be refused as "another bridge active".
  $r = Send-PipeLine $conn.Reader $conn.Writer $start
  Check 'failed-start-releases-session' (-not $r.Contains('another BlurLink bridge')) $r

  # Positive exclusivity: while the session name is held, `start` must be
  # refused with the "already active" message and must never reach the driver.
  # Skipped when this harness cannot create a Global object (that needs
  # SeCreateGlobalPrivilege; the elevated helper always has it).
  $held = $null
  try {
    $held = New-Object System.Threading.EventWaitHandle(
      $false, [System.Threading.EventResetMode]::ManualReset, 'Global\BlurLink-BridgeSession')
  } catch {
    $held = $null
  }
  if ($null -ne $held) {
    try {
      $r = Send-PipeLine $conn.Reader $conn.Writer $start
      Check 'second-bridge-refused-while-session-held' ($r.Contains('another BlurLink bridge')) $r
      $held.Dispose(); $held = $null
      $r = Send-PipeLine $conn.Reader $conn.Writer $start
      Check 'start-allowed-after-session-released' (-not $r.Contains('another BlurLink bridge')) $r
    } finally {
      if ($null -ne $held) { $held.Dispose() }
    }
  } else {
    Write-Host 'SKIP second-bridge-refused-while-session-held (cannot create a Global event unelevated)' -ForegroundColor Yellow
  }

  $bad = @{ type = 'start'; token = $token; hostOverlayIp = '999.1.1.1'; discoveryUdpPort = 0 } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $bad
  Check 'start-validation-error' ($r.Contains('"type":"error"')) $r

  # Task 12: a bridge start carrying the observe-only shape fields parses and
  # reaches the clean driver-error path unelevated (synthetic prefix, never
  # capture bytes). Same shape as the validation-path checks above.
  $shapeStart = @{
    type = 'start'; token = $token; hostOverlayIp = '100.96.47.177'
    discoveryUdpPort = 12345; broadcastDestination = '255.255.255.255'
    payloadPrefixHex = ''; preserveOriginalBroadcast = $true
    rateLimitPerSecond = 10; rateLimitBurst = 20; adapterIfIndex = 0
    expectedReplyLength = 160; expectedReplyPrefixHex = 'AA BB CC DD'
  } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $shapeStart
  if (Test-Path $dllNextToHelper) {
    $okShapeBridge = $r.Contains('"type":"error"') -and -not $r.Contains('not found')
  } else {
    $okShapeBridge = $r.Contains('"type":"error"') -and $r.Contains('WinDivert')
  }
  Check 'shape-fields-bridge-parses' $okShapeBridge $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'stop'; token = $token } | ConvertTo-Json -Compress)
  Check 'stop-ok' ($r.Contains('"type":"status"')) $r

  $badSniff = @{ type = 'sniff'; token = $token; broadcasts = @('999.1.1.1'); durationSec = 15; maxPackets = 200 } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $badSniff
  Check 'sniff-validation-error' ($r.Contains('"type":"error"')) $r

  $sniff = @{ type = 'sniff'; token = $token; broadcasts = @('255.255.255.255'); durationSec = 5; maxPackets = 50 } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $sniff
  # Unelevated (+ optional real DLL): SNIFF open is refused cleanly, never a crash.
  $okSniff = $r.Contains('"type":"error"') -or $r.Contains('"sniffActive":true')
  Check 'sniff-reaches-driver' $okSniff $r

  $inSniff = @{ type = 'sniff'; token = $token; broadcasts = @(); direction = 'in'; port = 50001; durationSec = 5; maxPackets = 50 } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $inSniff
  $okIn = $r.Contains('"type":"error"') -or $r.Contains('"sniffActive":true')
  Check 'inbound-sniff-reaches-driver' $okIn $r

  $badDir = @{ type = 'sniff'; token = $token; broadcasts = @(); direction = 'sideways'; port = 0; durationSec = 5; maxPackets = 50 } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $badDir
  Check 'sniff-direction-rejected' ($r.Contains('"type":"error"')) $r

  # --- host mode ---
  # Everything below runs unelevated, so it proves validation, the session guard
  # and the status contract. Whether host mode actually forwards a reply needs a
  # real driver and two machines: WinDivert does not capture loopback on the
  # inbound path, so a single-machine harness cannot feed host mode its inputs
  # (see TODO.md, "Task 14 amendment").
  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'get_status'; token = $token } | ConvertTo-Json -Compress)
  $j = $r | ConvertFrom-Json
  Check 'host-status-fields-present' ($null -ne $j.PSObject.Properties['hostActive'] -and
    $null -ne $j.PSObject.Properties['hostForwardsHeard'] -and
    $null -ne $j.PSObject.Properties['hostPlayers']) $r
  # `filter` is the bridge's, so host mode reports its own `hostFilter`. Without
  # it the Host tab's filter line is blank whenever host mode runs.
  Check 'host-status-has-own-filter-field' ($null -ne $j.PSObject.Properties['hostFilter']) $r
  Check 'host-inactive-before-start' ($j.hostActive -eq $false) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'start_host'; token = $token; discoveryUdpPort = 0; adapterIfIndex = 1 } | ConvertTo-Json -Compress)
  Check 'start-host-without-port-rejected' ($r.Contains('"type":"error"') -and $r.Contains('discovery port')) $r

  # The discovery port must never be BlurLink's own introduction port.
  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'start_host'; token = $token; discoveryUdpPort = 47811; adapterIfIndex = 1 } | ConvertTo-Json -Compress)
  Check 'start-host-rejects-the-announce-port' ($r.Contains('"type":"error"')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'start_host'; token = $token; discoveryUdpPort = 50001; adapterIfIndex = 0 } | ConvertTo-Json -Compress)
  Check 'start-host-without-adapter-rejected' ($r.Contains('"type":"error"')) $r

  $hostStart = @{ type = 'start_host'; token = $token; discoveryUdpPort = 50001
    adapterIfIndex = 1; rateLimitPerSecond = 10; rateLimitBurst = 20 } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $hostStart
  if (Test-Path $dllNextToHelper) {
    $okHost = $r.Contains('"type":"error"') -and -not $r.Contains('not found')
  } else {
    $okHost = $r.Contains('"type":"error"') -and $r.Contains('WinDivert')
  }
  Check 'start-host-reaches-driver' $okHost $r

  # Task 12: a host start carrying the observe-only shape fields parses and
  # reaches the clean driver-error path unelevated (synthetic prefix, never
  # capture bytes). Same shape as the validation-path checks above.
  $shapeHostStart = @{ type = 'start_host'; token = $token; discoveryUdpPort = 50001
    adapterIfIndex = 1; rateLimitPerSecond = 10; rateLimitBurst = 20
    expectedReplyLength = 160; expectedReplyPrefixHex = 'AA BB CC DD' } | ConvertTo-Json -Compress
  $r = Send-PipeLine $conn.Reader $conn.Writer $shapeHostStart
  if (Test-Path $dllNextToHelper) {
    $okShapeHost = $r.Contains('"type":"error"') -and -not $r.Contains('not found')
  } else {
    $okShapeHost = $r.Contains('"type":"error"') -and $r.Contains('WinDivert')
  }
  Check 'shape-fields-host-parses' $okShapeHost $r

  # A failed host start must release the exclusive session, like a bridge start.
  $r = Send-PipeLine $conn.Reader $conn.Writer $hostStart
  Check 'failed-host-start-releases-session' (-not $r.Contains('another BlurLink bridge')) $r
  $r = Send-PipeLine $conn.Reader $conn.Writer $start
  Check 'host-then-bridge-start-not-blocked' (-not $r.Contains('another BlurLink bridge')) $r

  # Positive exclusivity: host mode diverts, so it holds the same session.
  $heldHost = $null
  try {
    $heldHost = New-Object System.Threading.EventWaitHandle(
      $false, [System.Threading.EventResetMode]::ManualReset, 'Global\BlurLink-BridgeSession')
  } catch { $heldHost = $null }
  if ($null -ne $heldHost) {
    try {
      $r = Send-PipeLine $conn.Reader $conn.Writer $hostStart
      Check 'second-host-mode-refused-while-session-held' ($r.Contains('another BlurLink bridge')) $r
    } finally { $heldHost.Dispose() }
  } else {
    Write-Host 'SKIP second-host-mode-refused-while-session-held (cannot create a Global event unelevated)' -ForegroundColor Yellow
  }

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'revoke_host_player'; token = $token; overlayIp = '25.9.9.9' } | ConvertTo-Json -Compress)
  Check 'revoke-unknown-player-is-harmless' ($r.Contains('no such player')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'revoke_host_player'; token = $token } | ConvertTo-Json -Compress)
  Check 'revoke-missing-overlay-rejected' ($r.Contains('"type":"error"')) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'revoke_host_player'; token = $token; overlayIp = 'not-an-ip' } | ConvertTo-Json -Compress)
  Check 'revoke-bad-overlay-rejected' ($r.Contains('"type":"error"')) $r

  # Host mode's filter is its central diagnostic: the Host tab shows this line,
  # and it must be host mode's own, not the bridge's empty one. Starting host
  # mode needs the driver + elevation, so the unelevated harness reports SKIP
  # rather than a false pass.
  $r = Send-PipeLine $conn.Reader $conn.Writer $hostStart
  $jh = $r | ConvertFrom-Json
  if ($jh.type -eq 'status' -and $jh.hostActive) {
    Check 'host-filter-reported-when-running' ($jh.hostFilter.Contains('47811')) $r
    Check 'host-filter-is-not-the-bridge-filter' ($jh.hostFilter -ne $jh.filter) $r
  } else {
    Write-Host 'SKIP host-filter-reported-when-running (host mode needs the driver + elevation)' -ForegroundColor Yellow
  }

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'stop'; token = $token } | ConvertTo-Json -Compress)
  $j = $r | ConvertFrom-Json
  Check 'stop-halts-host-mode' ($j.type -eq 'status' -and $j.hostActive -eq $false) $r

  $r = Send-PipeLine $conn.Reader $conn.Writer (@{ type = 'shutdown'; token = $token } | ConvertTo-Json -Compress)
  Check 'shutdown-ack' ($r.Contains('shutdown')) $r
  try { $conn.Client.Dispose() } catch { }
  $exited = $proc.WaitForExit(10000)
  Check 'helper-exits-on-shutdown' ($exited -and $proc.ExitCode -eq 0) "exit=$($proc.ExitCode)"
  Start-Sleep -Milliseconds 500 # let async output events flush
  $dash = ($script:helperStdout -join "`n")
  Check 'dashboard-banner' ($dash.Contains('blurlink-net') -and $dash.Contains('waiting for GUI commands')) $dash
  Check 'dashboard-logs-sniff' ($dash.Contains('LISTEN REQUESTED')) 'missing sniff section line'
  Check 'dashboard-logs-stop' ($dash.Contains('STOP REQUESTED')) 'missing stop section line'
  Check 'dashboard-logs-host' ($dash.Contains('HOST MODE REQUESTED')) 'missing host mode section line'
  Check 'dashboard-leaks-no-token' (-not $dash.Contains($token)) 'TOKEN IN CONSOLE OUTPUT'
  Check 'dashboard-leaks-no-pipe' (-not $dash.Contains($pipe)) 'PIPE NAME IN CONSOLE OUTPUT'
  # --log-file mirror: the dashboard must also land on disk (metadata only).
  $logOk = (Test-Path $helperLog) -and
    ((Get-Content -Raw $helperLog).Contains('waiting for GUI commands'))
  Check 'dashboard-log-file-written' $logOk $helperLog
} finally {
  if (-not $proc.HasExited) { $proc.Kill($true) }
  $proc.Dispose()
}

if (Test-Path $helperLog) { Remove-Item -Force $helperLog -ErrorAction SilentlyContinue }

# --- watchdog scenario ---
$pipe2 = 'BlurLink-' + ([System.BitConverter]::ToString((1..8 | ForEach-Object { Get-Random -Max 256 }) -as [byte[]])).Replace('-', '')
$token2 = New-TokenHex
$proc2 = Start-Helper -Pipe $pipe2 -Token $token2 -Watchdog $WatchdogSec
try {
  $conn2 = Connect-Pipe -Pipe $pipe2
  $r = Send-PipeLine $conn2.Reader $conn2.Writer (@{ type = 'hello'; token = $token2; protocolVersion = 1 } | ConvertTo-Json -Compress)
  Check 'watchdog-hello-ok' ($r.Contains('"type":"status"')) $r
  $r = Send-PipeLine $conn2.Reader $conn2.Writer (@{ type = 'get_status'; token = $token2 } | ConvertTo-Json -Compress)
  Check 'watchdog-session-starts' ($r.Contains('"type":"status"')) $r
  # Simulate GUI death: close everything, stay silent.
  try { $conn2.Client.Dispose() } catch { }
  $exited2 = $proc2.WaitForExit(($WatchdogSec + 10) * 1000)
  Check 'watchdog-exits-helper' ($exited2 -and $proc2.ExitCode -eq 0) "exit=$($proc2.ExitCode)"
} finally {
  if (-not $proc2.HasExited) { $proc2.Kill($true) }
  $proc2.Dispose()
}

# --- log-file failure scenario: an unwritable --log-file must fail fast ---
$pipe3 = 'BlurLink-' + ([System.BitConverter]::ToString((1..8 | ForEach-Object { Get-Random -Max 256 }) -as [byte[]])).Replace('-', '')
$token3 = New-TokenHex
$proc3 = Start-Helper -Pipe $pipe3 -Token $token3 -Watchdog 10 -LogPath 'Z:\definitely\not\writable\helper.log'
try {
  $exited3 = $proc3.WaitForExit(5000)
  Check 'log-file-fail-fast' ($exited3 -and $proc3.ExitCode -eq 6) "exit=$($proc3.ExitCode)"
} finally {
  if (-not $proc3.HasExited) { $proc3.Kill($true) }
  $proc3.Dispose()
}

if ($failures -gt 0) { throw "$failures interop check(s) FAILED" }
Write-Host 'All interop checks passed.' -ForegroundColor Green
