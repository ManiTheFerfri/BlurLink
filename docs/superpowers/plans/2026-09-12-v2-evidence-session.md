# V2 Evidence Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer the three questions the product's central claim rests on — does a player's forward cross the overlay, which of the host's two embedded endpoints does the player's Blur dial, and does the lobby appear — and record the answers so the rewrite decision gate can be applied.

**Architecture:** No product code changes. Three small committed scripts (host capture, player capture, kit verification), one rewritten runbook, and a recording step. The session itself is driven by the two GUIs already built; the scripts exist so the two machines produce logs that can be lined up afterwards.

**Tech Stack:** PowerShell 7, the shipped `BlurLink.exe` portable build, `tools/hostsim/hostsim.exe` for captures, Blur on both machines, one overlay network spanning them.

**Spec:** `docs/superpowers/specs/2026-09-12-v2-design.md` §5 (questions, instruments, protocol, criteria, inconclusive branch, decision gate). This plan is the M1 row of that spec's §9.

## Global Constraints

- **Nothing about the product changes here.** If the session reveals a bug, it is recorded and fixed under its own task, not patched mid-session.
- **The host machine is the one with the lobby** (this machine); the friend is the **player**. The roles are not interchangeable mid-session: Host mode runs here, the bridge runs there.
- **Evidence discipline:** every claim in the outcome table points at a log line or a screenshot. Nothing is inferable "because it should".
- **Inconclusive is a valid, recorded result** (spec §5.5). It is never reported as a failure of the code, and never as a pass.
- **No payload rewriting** is implemented or implied by anything here, whatever the outcome.
- **Privacy:** the logs and screenshots carry real addresses (spec §8.1 has not been executed yet). They stay out of the repository; only scrubbed excerpts go in.
- **The friend's machine gets the self-contained build** so the session is not lost to a missing .NET Desktop Runtime.

---

## Task 1: Build and pre-verify the friend's kit

Nothing wastes a shared session like discovering the other machine cannot launch the app.

**Files:**
- Create: `scripts/session/verify-kit.ps1`
- Create: `docs/session/friend-instructions.md`

**Interfaces:**
- Consumes: `scripts/build-portable.ps1 -Full` (self-contained), `HelperLauncher.HasEmbeddedBridgeFiles`.
- Produces: a zip to send (`dist/BlurLink-Full/`) and a one-page instruction sheet; `scripts/session/verify-kit.ps1` prints `kit-ok` after checking the app launches, the helper resource is embedded, WinDivert is staged on first run, and a UAC elevation of the helper is offered.

- [ ] **Step 1: Build the self-contained portable**

```powershell
scripts/build-portable.ps1 -Full
```
Expected: `dist/BlurLink-Full/BlurLink.exe` (~140 MB, no runtime prerequisite). Record its size and SHA256.

- [ ] **Step 2: Verify the kit on this machine before sending it**

`scripts/session/verify-kit.ps1` checks, in order, and reports one `PASS`/`FAIL` line each:

1. the exe exists and reports its version;
2. the helper and both WinDivert files are embedded resources (`HelperLauncher.HasEmbeddedBridgeFiles`);
3. launching the app stages all three into `%LocalAppData%\BlurLink\bin` (delete that folder first so staging is actually exercised, not skipped);
4. the helper can be launched (UAC prompt appears) and `get_status` answers over the pipe.

Any `FAIL` here is fixed before the kit is sent — that is the entire point of the task.

- [ ] **Step 3: Write `docs/session/friend-instructions.md`**

Plain, numbered, and written for someone who has never seen the app:

1. Unzip anywhere; run `BlurLink.exe`. **Windows will warn about an unknown publisher** — that is expected for an unsigned build: *More info → Run anyway*. Windows Defender or the antivirus may also quarantine it because it installs a packet driver; allow it, and report it if it will not.
2. When it asks for Administrator, approve — that is the helper, not the whole app.
3. Choose your overlay adapter on the Join tab and confirm your overlay IP is the one you can ping on this network.
4. Open Blur once so its firewall rule exists, then start the LAN lobby search and confirm the game itself works.
5. Ping the host's overlay IP from Settings and report the result before we start.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Add the friend's kit instructions and a pre-flight verifier for it"
```

## Task 2: Host-side session tooling

**Files:**
- Create: `scripts/session/host-session.ps1`
- Test: run it unelevated (expect a clean refusal) and elevated with a dummy capture window

**Interfaces:**
- Consumes: `tools/hostsim/hostsim.exe` (`capture --src <ip> --port <discovery> --timestamps`), the helper log at `%LocalAppData%\BlurLink\logs\helper.log`.
- Produces: `out/session/host-<timestamp>/` containing `replies.cap` (the host's own outbound discovery replies — where the two embedded endpoints come from), `clock.txt` (local time, UTC time, offset), and `notes.md` (lobby name, discovery port, the two endpoints once read).

- [ ] **Step 1: Write the script**

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
  Host side of the two-machine evidence session. Opens an observe-only capture of
  THIS machine's own Blur discovery replies, so the two endpoints the reply embeds
  can be read off it, and records the clock offset for log correlation.

  Run in an ELEVATED PowerShell (captures need the driver). The GUI does the rest:
  start Host mode on the Host tab as usual. Nothing here injects or blocks.
#>
[CmdletBinding()]
param(
  [string]$HostIp = '',
  [int]$DiscoveryPort = 50001,
  [int]$Seconds = 600
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sim = Join-Path $root 'out/hostsim/hostsim.exe'
if (-not (Test-Path $sim)) { throw "hostsim not built at $sim (build it with scripts/build.ps1)" }

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host 'CANNOT RUN: captures need an elevated PowerShell (WinDivertOpen requires it).' -ForegroundColor Yellow
  exit 2
}

if ([string]::IsNullOrWhiteSpace($HostIp)) {
  $HostIp = [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
    Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
    ForEach-Object { $_.IPAddressToString } |
    Where-Object { $_ -notlike '127.*' } | Select-Object -First 1
}

$dir = Join-Path $root ('out/session/host-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $dir | Out-Null

@"
local:  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')
utc:    $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss.fff'))
offset: $([TimeZoneInfo]::Local.BaseUtcOffset)
tz:     $([TimeZoneInfo]::Local.Id)
"@ | Set-Content (Join-Path $dir 'clock.txt')

@"
# Host session notes

- host IP:        $HostIp
- discovery port: $DiscoveryPort
- lobby name:     <fill in before starting>
- started (local):$(Get-Date -Format 'HH:mm:ss')

## The two endpoints the reply embeds (read from replies.cap, then fill in)

- physical LAN address + port:
- overlay address + port:

## What to do

1. Start Host mode on the Host tab (UAC approved) and leave it running.
2. Tell the player to start their bridge, then refresh Blur's LAN list.
3. Paste the two endpoints above into the player's script as soon as they are known.
"@ | Set-Content (Join-Path $dir 'notes.md')

Write-Host "capturing this machine's own replies for $Seconds s -> $(Join-Path $dir 'replies.cap')" -ForegroundColor Cyan
& $sim capture --src $HostIp --port $DiscoveryPort --timestamps --seconds $Seconds |
  Tee-Object -FilePath (Join-Path $dir 'replies.cap')
Write-Host "done. read the two embedded endpoints out of replies.cap and put them in notes.md"
```

- [ ] **Step 2: Verify the refusal path unelevated**

Run unelevated: `pwsh -NoProfile -File scripts/session/host-session.ps1`
Expected: `CANNOT RUN: captures need an elevated PowerShell`, exit 2.

- [ ] **Step 3: Verify the capture path elevated, with a short window**

Run elevated: `pwsh -NoProfile -File scripts/session/host-session.ps1 -Seconds 20`
Expected: `out/session/host-<ts>/` contains `clock.txt` and `replies.cap`; the
capture reports its window; nothing is blocked or modified (SNIFF).

- [ ] **Step 4: Commit**

```bash
git add scripts/session/host-session.ps1
git commit -m "Add the host-side capture and clock record for the evidence session"
```

## Task 3: Player-side session tooling

**Files:**
- Create: `scripts/session/player-session.ps1`
- Test: run it unelevated (clean refusal), then elevated for 15 s

**Interfaces:**
- Consumes: `tools/hostsim/hostsim.exe`.
- Produces: `out/session/player-<timestamp>/` with `gameplay.cap` (any outbound traffic to the gameplay port — **both** candidate endpoints match one filter, which is the point), `clock.txt`, `notes.md`.

- [ ] **Step 1: Write the script**

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
  Player side of the two-machine evidence session: records any outbound traffic
  from this machine to Blur's gameplay port, which is what reveals WHICH of the
  host's two embedded endpoints this machine's Blur actually dials.

  --port matches udp.DstPort, so one capture covers the physical address and the
  overlay address at once - no filter needs to guess between them. Run ELEVATED.
#>
[CmdletBinding()]
param(
  [int]$GameplayPort = 3074,
  [int]$Seconds = 600
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sim = Join-Path $root 'out/hostsim/hostsim.exe'
if (-not (Test-Path $sim)) { throw "hostsim not built at $sim" }

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host 'CANNOT RUN: captures need an elevated PowerShell (WinDivertOpen requires it).' -ForegroundColor Yellow
  exit 2
}

$dir = Join-Path $root ('out/session/player-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $dir | Out-Null

@"
local:  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')
utc:    $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss.fff'))
offset: $([TimeZoneInfo]::Local.BaseUtcOffset)
tz:     $([TimeZoneInfo]::Local.Id)
"@ | Set-Content (Join-Path $dir 'clock.txt')

@"
# Player session notes

- gameplay port watched: $GameplayPort
- started (local):       $(Get-Date -Format 'HH:mm:ss')

## What to do

1. Join tab: host overlay IP, adapter selected, discovery port 50001, Start Bridge, approve UAC.
2. Play the session: open Blur's LAN list and refresh it a few times while the host watches.
3. Report back with: bridge counters (captured / forwarded / announcementsSent),
   this folder's gameplay.cap and clock.txt, and whether the lobby appeared.
"@ | Set-Content (Join-Path $dir 'notes.md')

Write-Host "capturing outbound traffic to port $GameplayPort for $Seconds s -> $(Join-Path $dir 'gameplay.cap')" -ForegroundColor Cyan
& $sim capture --port $GameplayPort --timestamps --seconds $Seconds |
  Tee-Object -FilePath (Join-Path $dir 'gameplay.cap')
Write-Host 'done. send gameplay.cap, clock.txt and notes.md back to the host.'
```

- [ ] **Step 2: Verify both paths**

Unelevated → `CANNOT RUN`, exit 2. Elevated with `-Seconds 15` → `gameplay.cap`
and `clock.txt` created; expect zero packets on a machine with no game running,
which is itself the correct result and a useful negative.

- [ ] **Step 3: Hand it over**

Copy `scripts/session/player-session.ps1` and `hostsim.exe` into the friend's kit
folder, and add to `docs/session/friend-instructions.md`: run the capture from an
elevated PowerShell, start it *before* refreshing Blur's list, and send the folder
back.

- [ ] **Step 4: Commit**

```bash
git add scripts/session/player-session.ps1 docs/session/friend-instructions.md
git commit -m "Add the player-side capture and the handover instructions"
```

## Task 4: Rewrite the runbook

The existing `docs/capture-day-checklist.md` is written for a same-LAN Wireshark
baseline and a local bridge test. The V2 session is a remote-overlay Host-mode
session with two captures, and the old document would send someone down the wrong
path.

**Files:**
- Modify: `docs/capture-day-checklist.md` (V2 protocol first; keep the Wireshark baseline as a dated appendix)

- [ ] **Step 1: Replace the front of the document**

Content, in order:

1. **Roles:** host = this machine (lobby + Host mode + `replies.cap`); player = the friend (bridge + `gameplay.cap`).
2. **Pre-flight, both machines:** overlay connected and ping-proven both ways; Blur launched once so its firewall rule exists; the LAN lobby created on the host and its name/time noted; ports `50001`/`47811` free; clock offset recorded by each script.
3. **Order of operations:** host starts `host-session.ps1` → host starts Host mode → player starts the bridge and `player-session.ps1` → player refreshes Blur's LAN list → host watches `forwards heard` → host reads the two embedded endpoints out of `replies.cap` → player's capture continues through a join attempt.
4. **The three questions**, each with the exact evidence that answers it (spec §5.1) and its pre-written criterion (spec §5.4).
5. **The inconclusive branch** (spec §5.5): up to three attempts, each with a fresh lobby and the injection window behaviour that once produced an answer; after three, record "not reproducible on demand" and say why real use is unaffected.
6. **Failure branches:** `forwards heard == 0` → tunnel problem, stop and diagnose before spending the window; lobby appears but join fails → read the player's `gameplay.cap` for which endpoint it dialled; nothing captured on the player at all → bridge misconfiguration, check the discovery port first.

- [ ] **Step 2: Keep the old baseline as an appendix**

Retitle it "Appendix: same-LAN Wireshark baseline (2026-09-09, kept for reference)" and note it is superseded for remote sessions because the transport is now the overlay, not the physical LAN.

- [ ] **Step 3: Commit**

```bash
git add docs/capture-day-checklist.md
git commit -m "Rewrite the capture-day checklist for the remote-overlay V2 session"
```

## Task 5: Run the session

This task is performed, not written. It has one rule: **if the host's
`forwards heard` is zero, stop early** and spend the remaining time on the tunnel
rather than on the game.

- [ ] **Step 1: Confirm both machines are ready**

Player reports: ping to the host's overlay IP succeeded, the app launched, the
helper was approved, the game's LAN list works. Host confirms: lobby open, Host
mode running, `replies.cap` capturing, `notes.md` filled with the lobby name.

- [ ] **Step 2: Start the player side and answer question (a)**

Player starts the bridge, then `player-session.ps1`, then refreshes Blur's LAN
list twice. Host reads `forwards heard`.
Expected/decision: **≥ 1 within 60 s of a refresh = yes**; a confirmed bridge
with zero across two refreshes = **no** → stop, and record the tunnel as the
finding.

- [ ] **Step 3: Read the two embedded endpoints off the host's reply**

From `replies.cap`, note the physical and overlay endpoints the reply embeds, put
them in `notes.md`, and tell the player which two addresses to look for (the
capture already matches both, so this is for the record and for interpretation).

- [ ] **Step 4: Answer question (c)**

Player: does the lobby list? Does a join complete? Screenshot both, with the time.

- [ ] **Step 5: Answer question (b)**

From `gameplay.cap`: which of the two endpoints received traffic. Apply the
criterion exactly (§5.4): traffic to the physical and none to the overlay = dials
the physical one; the reverse = dials the overlay; neither = **open**, not
"no".

## Task 6: Record the outcome and apply the gate

**Files:**
- Modify: `docs/packet-research.md`, `TODO.md`, `README.md`
- Create: `docs/session/<date>-two-machine-session.md` (the scrubbed session record)

**Interfaces:**
- Consumes: the two capture folders, both `clock.txt` files, the helper logs.
- Produces: the outcome table and the gate decision from spec §5.6.

- [ ] **Step 1: Write the session record**

Structure: the setup (builds, roles, overlay, clock offsets), the timeline
correlated across both machines, the outcome table with one row per question and
the evidence column pointing at a log line or screenshot, and an explicit
"What this does not prove" section.

- [ ] **Step 2: Apply the gate**

| Evidence for (b) | What happens next |
|---|---|
| dials the physical endpoint, join fails | the reflection workstream gets its own design note, a `SECURITY.md` entry and tests — opt-in per session, never a saved default |
| dials the overlay | the promise stands; record that no rewriter exists and why |
| neither | recorded open with the next step (a host-side capture of the join attempt) and no rewriting work |

- [ ] **Step 3: Update the standing docs**

`docs/packet-research.md`: the four V1 open questions are already answered there;
add the two-machine answers to (a) and (b), and retire "what is still unproven is
the tunnel half" from `README.md` — or sharpen it, if the answer was inconclusive.
`TODO.md`: the "Needs live Blur traffic" list gets struck through item by item,
with the ones that stay open marked as such and with their next step.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Record the two-machine session, close the open questions it answers, and apply the rewrite gate"
```

---

## Verification ledger

| Task | Status | Evidence to record |
|---|---|---|
| 1 | | `kit-ok` output; the zip's SHA256; the friend confirmed the app launched |
| 2 | | unelevated refusal (exit 2); an elevated 20 s run producing `replies.cap` + `clock.txt` |
| 3 | | unelevated refusal (exit 2); an elevated 15 s run producing `gameplay.cap` |
| 4 | | the runbook reads correctly to someone who was not in this conversation |
| 5 | | both capture folders, both clock files, the lobby/join screenshots |
| 6 | | the session record, the gate decision, and the docs updated in the same pass |

## Execution handoff

**Two execution options:**

1. **Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks.
2. **Inline Execution** — tasks executed in this session with checkpoints.

Which approach?
