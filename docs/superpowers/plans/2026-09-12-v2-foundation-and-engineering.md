# V2 Foundation and Engineering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give BlurLink a repository, an honest CI story and a tested session state model, so that every later V2 feature has one source of truth to render.

**Architecture:** Nothing in `Contracts`, `Core`'s existing units or the native helper is rewritten. A new `BlurLink.Core.Session` namespace owns what is true about a session (mode, phase, counters, capabilities, blocking reasons) and derives every user-visible sentence from it through one tested decision table. The WPF app is wired to that model in this plan as proof; the Avalonia shell that replaces it comes in the next stage's plan.

**Tech Stack:** .NET 10 LTS (`net10.0`, `net10.0-windows`), C# with `Nullable` and warnings-as-errors, xUnit (2.9.x party line), PowerShell 7 scripts, CMake + MSVC/MinGW for the native helper, GitHub Actions on `windows-latest`.

**Spec:** `docs/superpowers/specs/2026-09-12-v2-design.md` (this plan implements §8.1, §8.2, §8.3, §6.2, §6.5's spike, and §8.4 of it).

## Global Constraints

Copied from the spec; every task's requirements implicitly include these.

- **Framework floor:** .NET 10 LTS. `net10.0` for `BlurLink.Contracts` and `BlurLink.Core`, `net10.0-windows` for `BlurLink.Desktop` and the test project.
- **Windows only.** The output of this stage has a thin platform seam for testability, not for portability — no other OS target, no portability work.
- **Zero new runtime dependencies.** No DI container, no MVVM toolkit, no new NuGet packages in `Contracts`, `Core` or `Desktop`. Test-only packages are allowed.
- **No payload rewriting** unless the spec's decision gate (§5.6) opens; nothing in this stage touches packet contents.
- **No telemetry, no accounts, no backend, no network listener.** Diagnostics stay metadata-only; the redaction rule (no payload hex, no tokens, no pipe names in logs) is not weakened anywhere.
- **WinDivert 2.2.2 stays**; the native helper is not modified except where a task says so (no task here does).
- **Unsigned for now.** No signing, no MSIX, no auto-update in this stage.
- **Slim framework-dependent stays the deliverable**; the NativeAOT flavor is an experiment whose result is recorded, not a new default.
- **Honesty rules that bind the code and the docs:** exit code 2 means "could not run" and is never a pass; a withdrawn claim is never asserted without its retraction; no protocol constant without evidence.
- **Test count baseline:** 220 managed tests pass at plan time (2026-09-12). They must keep passing; tasks may add tests but must not delete an existing assertion without the task saying why.

## Stage map

The spec (§9) requires these stages as separate plans, because M1 is externally
gated and M3 depends on what M2 produces.

| Plan file | Stage | Entry criteria | Written |
|---|---|---|---|
| **this file** | M0 foundation + M2 engineering | spec approved | now |
| `docs/superpowers/plans/<date>-v2-evidence-session.md` | M1 evidence | a session window with the second machine | at the gate |
| `docs/superpowers/plans/<date>-v2-avalonia-surface.md` | M3 surface | this plan's Stage 1 complete | after Stage 1 |
| `docs/superpowers/plans/<date>-v2-release.md` | M4 release | M3 at parity | after M3 |

The evidence session's *preparation* is not in this plan on purpose: it is
scheduled work that belongs to whoever is holding the second machine, and it
reads the spec's §5 directly.

### Deliberately not in this plan

Listed so a gap is never mistaken for a decision:

| Spec item | Where it lives |
|---|---|
| §5 evidence session and its prep | the M1 plan, written when the session window opens |
| §7.1–§7.6 user-facing behaviour + the Avalonia shell | the M3 plan (it ports the views, then builds the bundles) |
| §8.2's UI CI job | the M3 plan — there is no Avalonia project to test before then |
| D12 retiring `-Full` / `-Sfx` | the M4 plan, once Task 11's AOT verdict exists |

---

# Stage 0 — M0 Foundation

## Task 1: Create the repository

**Files:**
- Create: `.gitignore`
- Create: `docs/REPO.md` (what is and is not in version control, and why)

**Interfaces:**
- Consumes: nothing.
- Produces: a git repository at the project root. Every later task ends with a real `git commit`, so this task is what turns the V1 "commit steps are N/A" wart into working history.

- [ ] **Step 1: Write `.gitignore`**

```gitignore
# Build output
bin/
obj/
out/
dist/

# Native build trees
build/
*.vcxproj.user

# Test and run artifacts (captures can contain real traffic excerpts)
*.cap
*.log

# Third-party binaries are downloaded and hash-verified, never committed.
# See third-party/WinDivert/README.md for the pinned version and SHA256s.
third-party/WinDivert/x64/

# Portable packaging staging (helper + WinDivert embedded at build time)
src/BlurLink.Desktop/Native/

# Editor noise
.vs/
.vscode/
*.user
```

- [ ] **Step 2: Write `docs/REPO.md`**

Content: the rule that captures, logs and third-party binaries never enter
history; where the pinned WinDivert hashes live; that `out/` holds real packet
captures and is ignored for that reason; and that `dist/` is build output.

- [ ] **Step 3: Initialise and confirm what would be added**

Run:
```bash
git init -b main
git add -A
git status --short | head -50
```
Expected: source, docs, scripts and profiles are staged; **no** `bin/`, `obj/`,
`out/`, `dist/` entries, no `WinDivert.dll`/`WinDivert64.sys`, no `*.cap`.

- [ ] **Step 4: Commit the existing tree, then this spec and plan**

```bash
git commit -m "Add BlurLink V1 as built: GUI, helper, tests, harness and research record"
git add docs/superpowers/specs/2026-09-12-v2-design.md docs/superpowers/plans/2026-09-12-v2-foundation-and-engineering.md docs/REPO.md
git commit -m "Add the V2 design spec and the foundation/engineering plan"
```

- [ ] **Step 5: Verify history is complete and clean**

Run: `git log --oneline` and `git status --porcelain`
Expected: two commits; the working tree clean.

## Task 2: Privacy scrub

The spec (D7, §8.1) makes the repo public, and the docs currently contain the
author's real addresses, network name, process IDs and host name — including the
host name inside a captured payload's hex (`4d0061006e0069`).

**Files:**
- Modify: `docs/packet-research.md`, `TODO.md`, `README.md`, `docs/user-guide.md`, `docs/capture-day-checklist.md`, `docs/troubleshooting.md`, `scripts/test-e2e.ps1`, `tools/hostsim/hostsim.cpp` (comments only)
- Create: `docs/privacy-scrub.md` (the record: what was replaced, with what, and why)
- Create: `scripts/check-docs-privacy.ps1`
- Test: `scripts/check-docs-privacy.ps1` run against a deliberate violation

**Interfaces:**
- Consumes: Task 1's repository.
- Produces: `scripts/check-docs-privacy.ps1` — takes no parameters, prints `privacy-ok` and exits 0, or prints each violation as `<file>:<line>: <literal>` and exits 1. Wired into CI by Task 3.

- [ ] **Step 1: Record the scrub rule in `docs/privacy-scrub.md`**

The rule from the spec: **structure, offsets and byte layout stay; identifiers are
replaced.** Table of every literal and its replacement:

| Literal kind | Example as found | Replacement |
|---|---|---|
| Overlay address | `10.88.14.114`, `10.88.14.200` | `10.0.0.10`, `10.0.0.200` |
| Physical LAN address | `192.168.1.116`, `192.168.1.200` | `192.168.0.10`, `192.168.0.200` |
| Overlay network name | the author's overlay name | `Example-Overlay` |
| Host name in payload hex | `4d0061006e0069` (UTF-16 `Mani`) | `48006f0073007400` (UTF-16 `Host`) |
| Game process id | `15832` | `12345` |
| Machine name | `Mani` | `Author` |
| Adapter names | the author's adapter names | `Ethernet`, `Example-Overlay` |

Payload hex must keep its length: a 5-character name stays a 5-character name.

- [ ] **Step 2: Apply the scrub**

Search and replace each literal across the listed files. Read every hunk: the
rule requires that offsets, lengths and the surrounding prose stay true, so a
replacement that changes a byte count is wrong.

Run this to find candidates:
```bash
grep -rn "10\.88\.14\.\|192\.168\.1\.\|PacketRaft\|15832\|Mani" --include='*.md' --include='*.ps1' --include='*.cpp' --include='*.json' . | grep -v '^./out/' | grep -v '^./bin/'
```

- [ ] **Step 3: Write the checker**

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
  Fails when a scrubbed identifier reappears in the repository.
  See docs/privacy-scrub.md. Structure and offsets are preserved by the scrub,
  so only identifiers are banned.
#>
[CmdletBinding()]
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'

# literal -> why it must not return (docs/privacy-scrub.md)
$banned = [ordered]@{
  '10.88.14.'    = 'author overlay address'
  '192.168.1.'   = 'author physical LAN address'
  'PacketRaft'   = 'author overlay network name'
  '4d0061006e0069' = 'author host name inside captured payload hex'
  '15832'        = 'author game process id'
}

$extensions = @('.md', '.ps1', '.cpp', '.h', '.json', '.cs', '.xaml', '.csproj')
$skipDirs   = @('out', 'dist', 'bin', 'obj', '.git', 'third-party')

$violations = foreach ($file in Get-ChildItem -Path $Root -Recurse -File |
    Where-Object { $extensions -contains $_.Extension }) {
  $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
  if ($skipDirs | Where-Object { $relative -like "$_*" -or $relative -like "*\$_\*" -or $relative -like "*/$_/*" }) { continue }
  if ($relative -eq 'docs/privacy-scrub.md' -or $relative -eq 'scripts/check-docs-privacy.ps1') { continue }
  $lineNo = 0
  foreach ($line in Get-Content -LiteralPath $file.FullName) {
    $lineNo++
    foreach ($literal in $banned.Keys) {
      if ($line.Contains($literal)) {
        "$relative`:$lineNo`: $literal ($($banned[$literal]))"
      }
    }
  }
}

if ($violations) { $violations; exit 1 }
Write-Host 'privacy-ok'
exit 0
```

- [ ] **Step 4: Run it — expect a pass — then prove it can fail**

Run: `pwsh -NoProfile -File scripts/check-docs-privacy.ps1`
Expected: `privacy-ok`, exit 0.

Then temporarily add the line `10.88.14.114` to `README.md`, run again, expect a
`README.md:<n>: 10.88.14.  (author overlay address)` line and exit 1, then remove
the line and confirm the pass returns.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Scrub identifying literals from docs and scripts, and check them in CI-able form"
```

## Task 3: Docs-consistency checks

The spec's §8.5: a registry of withdrawn claims plus a cross-check of documented
constants. This exists because the retracted "a hosting Blur is silent" claim
lived in three files at once.

**Files:**
- Create: `docs/claims-registry.json`
- Create: `scripts/check-doc-claims.ps1`
- Modify: `scripts/test.ps1` (run the checks as part of one command)
- Modify: `.github/workflows/ci.yml` (a `docs` job)

**Interfaces:**
- Consumes: Task 2's checker (same shape: silent on success, exit 1 on violation).
- Produces: `scripts/check-doc-claims.ps1`, no parameters, prints `claims-ok` or one line per violation and exits 1.

- [ ] **Step 1: Write the registry**

```json
{
  "retractedClaims": [
    {
      "id": "hosting-is-silent",
      "claim": "a hosting Blur is silent",
      "retractedOn": "2026-09-12",
      "reason": "A 20 s sample landed in a gap; the timestamped 45 s window caught the host's Blur querying with the host machine untouched.",
      "allowedIn": ["docs/packet-research.md", "TODO.md", "README.md"]
    }
  ],
  "documentedConstants": [
    { "name": "discovery port default", "value": "50001", "inCode": "src/BlurLink.Contracts/Constants.cs", "mustAppearIn": ["docs/user-guide.md"] },
    { "name": "introduction port", "value": "47811", "inCode": "src/BlurLink.Net/include/blurlink/announce.h", "mustAppearIn": ["docs/architecture.md"] },
    { "name": "protocol version", "value": "1", "inCode": "src/BlurLink.Contracts/Constants.cs", "mustAppearIn": [] }
  ]
}
```

`allowedIn` is the list of files where the claim may appear **at all** — inside a
retraction note it is allowed; anywhere else it is a violation. Add files to that
list only when the mention is historical.

- [ ] **Step 2: Write the checker**

```powershell
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
$violations = @()

foreach ($entry in $registry.retractedClaims) {
  $allowed = @($entry.allowedIn)
  foreach ($file in Get-ChildItem -Path $Root -Recurse -File -Include '*.md' |
      Where-Object { $_.FullName -notmatch '\\(out|dist|bin|obj|\.git)\\' }) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
    if ($allowed -contains $relative) { continue }
    $lineNo = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
      $lineNo++
      if ($line.ToLowerInvariant().Contains($entry.claim.ToLowerInvariant())) {
        $violations += "$relative`:$lineNo`: asserts retracted claim '$($entry.id)'"
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
```

- [ ] **Step 3: Run it — expect a pass — then prove it can fail**

Run: `pwsh -NoProfile -File scripts/check-doc-claims.ps1`
Expected: `claims-ok`.
Then append `a hosting Blur is silent` to `docs/user-guide.md`, expect a
violation line for that file, remove it, confirm the pass.

- [ ] **Step 4: Wire both checkers into `scripts/test.ps1` and CI**

In `scripts/test.ps1`, after the managed tests:

```powershell
Write-Host "==> docs checks" -ForegroundColor Cyan
& (Join-Path $root 'scripts/check-docs-privacy.ps1')
if ($LASTEXITCODE -ne 0) { throw "privacy check failed" }
& (Join-Path $root 'scripts/check-doc-claims.ps1')
if ($LASTEXITCODE -ne 0) { throw "docs claim check failed" }
```

In `.github/workflows/ci.yml`, add a job:

```yaml
  docs:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - name: Privacy and claim checks
        shell: pwsh
        run: |
          scripts/check-docs-privacy.ps1
          scripts/check-doc-claims.ps1
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Check the docs against the code: retracted claims and documented constants"
```

## Task 4: Move to .NET 10 LTS

**Files:**
- Modify: `global.json`, `src/BlurLink.Contracts/BlurLink.Contracts.csproj`, `src/BlurLink.Core/BlurLink.Core.csproj`, `src/BlurLink.Desktop/BlurLink.Desktop.csproj`, `tests/BlurLink.Core.Tests/BlurLink.Core.Tests.csproj`, `.github/workflows/ci.yml`, `README.md` (requirements section)
- Test: the whole managed suite

**Interfaces:**
- Consumes: nothing.
- Produces: `net10.0` / `net10.0-windows` targets; every later task builds on them.

- [ ] **Step 1: Pin the SDK**

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

- [ ] **Step 2: Retarget the four projects**

`net8.0` → `net10.0` in `BlurLink.Contracts.csproj` and `BlurLink.Core.csproj`;
`net8.0-windows` → `net10.0-windows` in `BlurLink.Desktop.csproj` and
`BlurLink.Core.Tests.csproj`. Change nothing else in those files.

- [ ] **Step 3: Bring the test packages up**

Run:
```bash
dotnet list src/BlurLink.Core.Tests/BlurLink.Core.Tests.csproj package --outdated
```
Update `Microsoft.NET.Test.Sdk` to the newest stable the command reports (it must
support .NET 10), and keep `xunit` on the 2.9.x line. Record the chosen versions
in the commit message. If `--outdated` reports nothing while the build fails, the
installed SDK's own template versions are the source of truth — use
`dotnet new xunit --dry-run` to read them.

- [ ] **Step 4: Build and run everything**

Run: `dotnet build BlurLink.sln -c Release --nologo` then `dotnet test BlurLink.sln -c Release --no-build --nologo`
Expected: build clean under warnings-as-errors; **220 passed, 0 failed**.

- [ ] **Step 5: Update CI and the README**

`.github/workflows/ci.yml`: `dotnet-version: 10.0.x` in the `managed` job.
`README.md`: requirements say .NET 10 (Desktop Runtime for the portable build),
and any 3.5 MB size claim is re-measured by Task 11's script rather than assumed.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Move to .NET 10 LTS: four projects retargeted, suite green"
```

## Task 5: Find out whether the driver harness can run on GitHub

Spec D5. The honest outcome is unknown: the runners are administrators, but
whether WinDivert installs and injection works there has never been tried. Both
branches are outcomes, and neither is a pass.

**Files:**
- Create: `.github/workflows/driver.yml`
- Create: `docs/driver-ci-experiment.md` (the recorded result, either way)

**Interfaces:**
- Consumes: `scripts/test-e2e.ps1` (exit 0 = pass, 1 = failed, 2 = could not run) and its `-PreflightOnly` mode from Task 6.
- Produces: a recorded verdict of `runs`, `cannot-run`, or `fails`, with the artifacts that justify it.

- [ ] **Step 1: Write the workflow**

```yaml
name: driver

on:
  workflow_dispatch:
  push:
    paths:
      - 'src/BlurLink.Net/**'
      - 'scripts/test-e2e.ps1'
      - 'tools/hostsim/**'
      - '.github/workflows/driver.yml'

jobs:
  driver-harness:
    runs-on: windows-latest
    timeout-minutes: 30
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - name: Build the helper (MSVC x64)
        run: |
          cmake -S src/BlurLink.Net -B out/native -DCMAKE_BUILD_TYPE=Release
          cmake --build out/native --config Release
      - name: Build the injector
        run: |
          cmake -S tools/hostsim -B out/hostsim -DCMAKE_BUILD_TYPE=Release
          cmake --build out/hostsim --config Release
      - name: Run the elevated harness
        id: harness
        shell: pwsh
        run: |
          $exe = 'out/native/Release/blurlink-net.exe'
          $sim = 'out/hostsim/Release/hostsim.exe'
          try {
            scripts/test-e2e.ps1 -HelperExe $exe -SimExe $sim 2>&1 |
              Tee-Object -FilePath out/driver-run.log
            "exit=$LASTEXITCODE" | Out-File -Append out/driver-run.log
          } catch {
            $_ | Out-String | Out-File -Append out/driver-run.log
            "exit=exception" | Out-File -Append out/driver-run.log
          }
          Get-Content out/driver-run.log | Select-Object -Last 40
      - name: Record the verdict
        shell: pwsh
        run: |
          $log = Get-Content -Raw out/driver-run.log -ErrorAction SilentlyContinue
          if ($log -match 'exit=0') { Write-Host 'VERDICT: runs (all checks passed)' }
          elseif ($log -match 'CANNOT RUN') { Write-Host 'VERDICT: cannot-run (environment refuses elevation or the driver)'; exit 0 }
          else { Write-Host 'VERDICT: fails (the harness ran and reported failures)'; exit 1 }
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: driver-run-log
          path: out/driver-run.log
```

Note the branch structure: `cannot-run` is a **recorded outcome**, not a failure
of the code, and `fails` is a real failure. This is the same three-way honesty the
script itself uses.

- [ ] **Step 2: Run it on the repository**

Run: push the workflow and trigger it (`gh workflow run driver.yml`) or let the
path filter fire. Read the artifact.

- [ ] **Step 3: Record the result in `docs/driver-ci-experiment.md`**

Whichever verdict arrived, with: the runner image, the exact output lines that
justify it (the `WinDivertOpen` error code if it refused, or the 42-check
summary if it ran), the date, and the consequence:

- `runs` → the job becomes the permanent home for the harness (Task 12 wires it into the release gate), and `TODO.md`'s "harness verified manually only" entry is retired.
- `cannot-run` → the spec's R5 fallback applies: the manual-but-provable protocol, and this file becomes the evidence for why.
- `fails` → a real bug or a real environment incompatibility; investigate before deciding, and do not silence the job.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Try the driver harness on GitHub-hosted runners and record the verdict"
```

## Task 6: Preflight-only mode for the elevated scripts

Spec §8.3. Today an environment problem and a product problem look similar from
outside, and the environment checks cannot be run without admin (which is also
what made Task 5's experiment awkward).

**Files:**
- Modify: `scripts/test-e2e.ps1` (parameter + early exit)
- Test: run it unelevated, and with a live Blur

**Interfaces:**
- Consumes: the existing precondition blocks in `scripts/test-e2e.ps1` (Blur guard, elevation gate, artifact checks, WinDivert staging).
- Produces: `-PreflightOnly` — runs every precondition, prints one line per check as `PASS <name>` / `FAIL <name>: <reason>`, and exits 0 when all pass, 2 when any precondition cannot be met. It never opens a handle and never injects.

- [ ] **Step 1: Add the parameter**

```powershell
  # Report the environment preconditions and exit without opening a handle or
  # injecting anything. Exit 0 = the environment is ready, 2 = it is not.
  [switch]$PreflightOnly
```

- [ ] **Step 2: Make each precondition report through one helper**

```powershell
$preflightFailures = @()
function Test-Precondition {
  param([string]$Name, [bool]$Ok, [string]$Reason = '')
  if ($Ok) { Write-Host "PASS $Name" -ForegroundColor Green }
  else {
    Write-Host "FAIL $Name`: $Reason" -ForegroundColor Yellow
    $script:preflightFailures += $Name
  }
  return $Ok
}
```

Convert the existing gates to call it: Blur not running, elevated, helper
present, injector present, WinDivert DLL and driver staged, host IPv4 address
resolved, discovery port in range. The elevation gate is the pattern for all of
them:

```powershell
if (-not (Test-Precondition 'elevated' $isAdmin 'WinDivertOpen needs Administrator - start an elevated PowerShell')) {
  if ($PreflightOnly) { } else { exit 2 }
}
```

(`$isAdmin` is already computed above; the `if ($PreflightOnly) { }` branch lets
preflight collect every failure instead of exiting at the first one.)

- [ ] **Step 3: Exit right after the checks when asked**

```powershell
if ($PreflightOnly) {
  if ($preflightFailures.Count -gt 0) {
    Write-Host "PREFLIGHT FAILED ($($preflightFailures.Count) precondition(s) unmet)" -ForegroundColor Yellow
    exit 2
  }
  Write-Host 'PREFLIGHT OK — environment ready for the elevated run' -ForegroundColor Green
  exit 0
}
```

- [ ] **Step 4: Verify both branches for real**

Run unelevated: `pwsh -NoProfile -File scripts/test-e2e.ps1 -PreflightOnly`
Expected: `PASS` lines for the artifacts, `FAIL elevated: ...`, `PREFLIGHT FAILED`, exit 2.

Close Blur (or leave it running and note the `FAIL blur-not-running` line), then
run from an elevated prompt and expect `PREFLIGHT OK`, exit 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add a preflight-only mode so environment checks need no admin shell"
```

---

# Stage 1 — M2 Engineering

Stage 0's exit criteria must be met first: repository with the scrub applied,
.NET 10 green, docs checks in CI, the harness verdict recorded.

## Task 7: Session state and counters

**Files:**
- Create: `src/BlurLink.Core/Session/SessionState.cs`
- Test: `tests/BlurLink.Core.Tests/SessionStateTests.cs`

**Interfaces:**
- Consumes: `BlurLink.Contracts.IpcStatusResponse`, `HostPlayerStatus`.
- Produces:
  - `enum SessionMode { None, Sniff, Bridge, Host }`
  - `enum SessionPhase { Idle, Starting, Running, Stopping, Failed }`
  - `record SessionCapabilities(bool HelperPresent, bool OverlayAddressKnown, bool AdapterSelected, bool GameRunning)`
  - `record SessionCounters(...)` with `static SessionCounters Empty` and `static SessionCounters From(IpcStatusResponse status)`
  - `record BlockingReason(string Code, string Text, string Fix)`
  - `record PlayerFact(string OverlayIp, string LanIp, int BlurSourcePort, long ForwardsHeard, bool InFilter)`
  - `record SessionState(SessionMode, SessionPhase, SessionCounters, IReadOnlyList<PlayerFact>, IReadOnlyList<BlockingReason>, string LastError)` with `static SessionState Initial`, `IsRunning`, `IsBusy`

- [ ] **Step 1: Write the failing test**

```csharp
using BlurLink.Contracts;
using BlurLink.Core.Session;

namespace BlurLink.Core.Tests;

public sealed class SessionStateTests
{
    [Fact]
    public void From_MapsEveryCounter_SoANewCounterCannotBeSilentlyDropped()
    {
        var status = new IpcStatusResponse
        {
            Captured = 1, Forwarded = 2, Reinjected = 3, Dropped = 4, DedupSkipped = 5,
            AnnouncementsSent = 6, HostForwardsHeard = 7, HostRepliesForwarded = 8,
            HostBroadcastReplies = 9, HostAmbiguousReplies = 10,
        };

        var counters = SessionCounters.From(status);

        Assert.Equal(1, counters.Captured);
        Assert.Equal(2, counters.Forwarded);
        Assert.Equal(3, counters.Reinjected);
        Assert.Equal(4, counters.Dropped);
        Assert.Equal(5, counters.DedupSkipped);
        Assert.Equal(6, counters.AnnouncementsSent);
        Assert.Equal(7, counters.HostForwardsHeard);
        Assert.Equal(8, counters.HostRepliesForwarded);
        Assert.Equal(9, counters.HostBroadcastReplies);
        Assert.Equal(10, counters.HostAmbiguousReplies);
    }

    [Fact]
    public void Initial_IsIdleAndNotBusy()
    {
        Assert.Equal(SessionMode.None, SessionState.Initial.Mode);
        Assert.Equal(SessionPhase.Idle, SessionState.Initial.Phase);
        Assert.False(SessionState.Initial.IsBusy);
        Assert.False(SessionState.Initial.IsRunning);
    }

    [Theory]
    [InlineData(SessionPhase.Starting, true)]
    [InlineData(SessionPhase.Stopping, true)]
    [InlineData(SessionPhase.Running, false)]
    [InlineData(SessionPhase.Failed, false)]
    public void IsBusy_CoversStartingAndStopping(SessionPhase phase, bool expected)
        => Assert.Equal(expected, (SessionState.Initial with { Phase = phase }).IsBusy);
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter SessionStateTests -c Release`
Expected: FAIL — `SessionState`/`SessionCounters` do not exist.

- [ ] **Step 3: Implement**

```csharp
using BlurLink.Contracts;

namespace BlurLink.Core.Session;

public enum SessionMode { None, Sniff, Bridge, Host }

public enum SessionPhase { Idle, Starting, Running, Stopping, Failed }

public sealed record SessionCapabilities(
    bool HelperPresent,
    bool OverlayAddressKnown,
    bool AdapterSelected,
    bool GameRunning)
{
    public static readonly SessionCapabilities Unknown = new(false, false, false, false);
}

/// <summary>Helper counters, copied one for one. Never payloads, never game data.</summary>
public sealed record SessionCounters(
    long Captured,
    long Forwarded,
    long Reinjected,
    long Dropped,
    long DedupSkipped,
    long AnnouncementsSent,
    long HostForwardsHeard,
    long HostRepliesForwarded,
    long HostBroadcastReplies,
    long HostAmbiguousReplies)
{
    public static readonly SessionCounters Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static SessionCounters From(IpcStatusResponse s) => new(
        s.Captured, s.Forwarded, s.Reinjected, s.Dropped, s.DedupSkipped,
        s.AnnouncementsSent, s.HostForwardsHeard, s.HostRepliesForwarded,
        s.HostBroadcastReplies, s.HostAmbiguousReplies);
}

/// <summary>A reason a session cannot run, with the fix the user can act on.</summary>
public sealed record BlockingReason(string Code, string Text, string Fix);

/// <summary>One player the host accepted, as the Host tab needs it.</summary>
public sealed record PlayerFact(
    string OverlayIp,
    string LanIp,
    int BlurSourcePort,
    long ForwardsHeard,
    bool InFilter);

public sealed record SessionState(
    SessionMode Mode,
    SessionPhase Phase,
    SessionCounters Counters,
    IReadOnlyList<PlayerFact> Players,
    IReadOnlyList<BlockingReason> BlockingReasons,
    string LastError)
{
    public static readonly SessionState Initial = new(
        SessionMode.None, SessionPhase.Idle, SessionCounters.Empty,
        Array.Empty<PlayerFact>(), Array.Empty<BlockingReason>(), string.Empty);

    public bool IsRunning => Phase == SessionPhase.Running;

    public bool IsBusy => Phase is SessionPhase.Starting or SessionPhase.Stopping;

    public static SessionState FromStatus(
        IpcStatusResponse status,
        SessionPhase phase,
        IReadOnlyList<BlockingReason> blocking) => new(
        status.HostActive ? SessionMode.Host
            : status.SniffActive ? SessionMode.Sniff
            : status.Active ? SessionMode.Bridge
            : SessionMode.None,
        phase,
        SessionCounters.From(status),
        status.HostPlayers
            .Select(p => new PlayerFact(p.OverlayIp, p.LanIp, p.BlurSourcePort, p.ForwardsHeard, p.InFilter))
            .ToList(),
        blocking,
        status.LastError);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter SessionStateTests -c Release`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/BlurLink.Core/Session/SessionState.cs tests/BlurLink.Core.Tests/SessionStateTests.cs
git commit -m "Add the session state model: mode, phase, counters and blocking reasons"
```

## Task 8: The session story table

This is the task that makes spec §7.2 true: every user-visible sentence is
produced here and asserted below.

**Files:**
- Create: `src/BlurLink.Core/Session/SessionStory.cs`
- Test: `tests/BlurLink.Core.Tests/SessionStoryTests.cs`

**Interfaces:**
- Consumes: Task 7's types.
- Produces: `enum StorySeverity { Neutral, Progress, Attention }`,
  `record SessionStory(string Code, string Headline, string Detail, string SuggestedAction, StorySeverity Severity)`,
  `static class SessionStoryTable { static SessionStory Describe(SessionState state) }`.

- [ ] **Step 1: Write the failing tests — one per sentence**

```csharp
using BlurLink.Core.Session;

namespace BlurLink.Core.Tests;

public sealed class SessionStoryTests
{
    private static SessionState Bridge(long captured, long forwarded) => SessionState.Initial with
    {
        Mode = SessionMode.Bridge,
        Phase = SessionPhase.Running,
        Counters = SessionCounters.Empty with { Captured = captured, Forwarded = forwarded },
    };

    [Fact]
    public void BlockingReason_WinsOverEverything()
    {
        var state = SessionState.Initial with
        {
            BlockingReasons = new[] { new BlockingReason("helper-missing", "The helper is not available.", "Rebuild it with scripts/build.ps1.") },
        };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("blocked", story.Code);
        Assert.Equal("Not ready yet", story.Headline);
        Assert.Equal("The helper is not available.", story.Detail);
        Assert.Equal("Rebuild it with scripts/build.ps1.", story.SuggestedAction);
        Assert.Equal(StorySeverity.Attention, story.Severity);
    }

    [Fact]
    public void Failed_ShowsTheError()
    {
        var story = SessionStoryTable.Describe(
            SessionState.Initial with { Phase = SessionPhase.Failed, LastError = "another BlurLink bridge is already active" });

        Assert.Equal("failed", story.Code);
        Assert.Contains("another BlurLink bridge is already active", story.Detail);
    }

    [Fact]
    public void BridgeWithNothingCaptured_TellsTheUserToRefreshTheGame()
    {
        var story = SessionStoryTable.Describe(Bridge(captured: 0, forwarded: 0));

        Assert.Equal("bridge-idle", story.Code);
        Assert.Contains("refresh", story.SuggestedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BridgeSeeingButNotForwarding_SaysSoWithoutBlamingTheHost()
    {
        var story = SessionStoryTable.Describe(Bridge(captured: 4, forwarded: 0));

        Assert.Equal("bridge-seen-only", story.Code);
        Assert.Contains("discovery port", story.Detail);
    }

    [Fact]
    public void BridgeForwarding_PointsAtHostModeWhenTheLobbyIsMissing()
    {
        var story = SessionStoryTable.Describe(Bridge(captured: 4, forwarded: 4));

        Assert.Equal("bridge-forwarding", story.Code);
        Assert.Contains("Host mode", story.Detail);
    }

    [Fact]
    public void HostWithNoPlayerYet_IsNormal_NotAFailure()
    {
        var state = SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("host-waiting", story.Code);
        Assert.Equal(StorySeverity.Neutral, story.Severity);
        Assert.Contains("search", story.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostHearingNothingFromAPlayer_DoesNotClaimThePlayerIsBlocked()
    {
        var state = SessionState.Initial with
        {
            Mode = SessionMode.Host,
            Phase = SessionPhase.Running,
            Players = new[] { new PlayerFact("10.0.0.200", "192.168.0.200", 50001, ForwardsHeard: 0, InFilter: true) },
        };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("host-no-forward", story.Code);
        Assert.Contains("has not searched yet", story.Detail);
    }

    [Fact]
    public void HostHeardButNotYetForwarded_SaysWhatIsMissing()
    {
        var state = SessionState.Initial with
        {
            Mode = SessionMode.Host,
            Phase = SessionPhase.Running,
            Counters = SessionCounters.Empty with { HostForwardsHeard = 3, HostRepliesForwarded = 0 },
        };

        Assert.Equal("host-heard-no-reply", SessionStoryTable.Describe(state).Code);
    }

    [Fact]
    public void HostForwarding_SaysTheReplyIsOnItsWay()
    {
        var state = SessionState.Initial with
        {
            Mode = SessionMode.Host,
            Phase = SessionPhase.Running,
            Counters = SessionCounters.Empty with { HostForwardsHeard = 3, HostRepliesForwarded = 3 },
        };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("host-forwarding", story.Code);
        Assert.Equal(StorySeverity.Progress, story.Severity);
    }

    [Fact]
    public void Idle_SaysNothingIsRunning()
        => Assert.Equal("idle", SessionStoryTable.Describe(SessionState.Initial).Code);

    [Fact]
    public void EveryCodeIsDistinct()
    {
        var codes = new[]
        {
            SessionState.Initial with { BlockingReasons = new[] { new BlockingReason("x", "y", "z") } },
            SessionState.Initial with { Phase = SessionPhase.Failed },
            Bridge(0, 0), Bridge(4, 0), Bridge(4, 4),
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running },
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running, Players = new[] { new PlayerFact("a", "b", 1, 0, true) } },
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running, Counters = SessionCounters.Empty with { HostForwardsHeard = 1 } },
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running, Counters = SessionCounters.Empty with { HostForwardsHeard = 1, HostRepliesForwarded = 1 } },
            SessionState.Initial,
        }.Select(SessionStoryTable.Describe).Select(s => s.Code).ToArray();

        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter SessionStoryTests -c Release`
Expected: FAIL — `SessionStoryTable` does not exist.

- [ ] **Step 3: Implement the table**

```csharp
namespace BlurLink.Core.Session;

public enum StorySeverity { Neutral, Progress, Attention }

public sealed record SessionStory(
    string Code,
    string Headline,
    string Detail,
    string SuggestedAction,
    StorySeverity Severity);

/// <summary>
/// The only place user-visible session sentences are written. Rules are ordered;
/// the first match wins, so the table stays deterministic and testable.
/// </summary>
public static class SessionStoryTable
{
    public static SessionStory Describe(SessionState s)
    {
        if (s.BlockingReasons.Count > 0)
        {
            var first = s.BlockingReasons[0];
            return new SessionStory("blocked", "Not ready yet", first.Text, first.Fix, StorySeverity.Attention);
        }

        return s.Phase switch
        {
            SessionPhase.Failed => new SessionStory(
                "failed", "The session stopped with an error",
                string.IsNullOrWhiteSpace(s.LastError) ? "No error detail was reported." : s.LastError,
                "Open Diagnostics for the helper log, then start again.", StorySeverity.Attention),

            SessionPhase.Starting => new SessionStory(
                "starting", "Starting…",
                "Approve the Windows prompt if one appears — the helper needs Administrator.",
                string.Empty, StorySeverity.Progress),

            SessionPhase.Stopping => new SessionStory(
                "stopping", "Stopping…", "Waiting for the helper to close its session.",
                string.Empty, StorySeverity.Progress),

            SessionPhase.Running when s.Mode == SessionMode.Sniff => new SessionStory(
                "sniff", "Listening for broadcasts",
                "Refresh the game's LAN list now so the listener can hear the discovery packet.",
                string.Empty, StorySeverity.Progress),

            SessionPhase.Running when s.Mode == SessionMode.Bridge => BridgeStory(s),

            SessionPhase.Running when s.Mode == SessionMode.Host => HostStory(s),

            _ => new SessionStory(
                "idle", "Nothing is running",
                "No session is active, so no packets are being captured or forwarded.",
                "Start a session, or run the first-run check.", StorySeverity.Neutral),
        };
    }

    private static SessionStory BridgeStory(SessionState s) => s.Counters.Captured switch
    {
        0 => new SessionStory(
            "bridge-idle", "Nothing captured yet",
            "Blur's discovery packet has not been seen on this machine.",
            "Open Blur's LAN browser and refresh the server list.", StorySeverity.Neutral),

        _ when s.Counters.Forwarded == 0 => new SessionStory(
            "bridge-seen-only", "Blur is being seen, but nothing is forwarded yet",
            "Packets reached the capture filter but none matched the discovery port or the payload gate.",
            "Check the discovery port in the profile — a wrong port is the usual cause.", StorySeverity.Attention),

        _ => new SessionStory(
            "bridge-forwarding", "You are reaching the host",
            "Your query is being forwarded over the overlay. If the lobby does not appear, the host is not sending replies back — that is what Host mode fixes.",
            "If the lobby stays empty, ask the host to start Host mode.", StorySeverity.Progress),
    };

    private static SessionStory HostStory(SessionState s)
    {
        // Order matters: the per-player test must come before the reply test, or
        // "heard but nothing forwarded back" is unreachable whenever a player is
        // in the roster with a zero count.
        if (s.Players.Count == 0 && s.Counters.HostForwardsHeard == 0)
        {
            return new SessionStory(
                "host-waiting", "Waiting for a player",
                "No player has announced itself yet. This is normal until someone searches for your game.",
                "Tell your friend to open Blur's LAN list.", StorySeverity.Neutral);
        }

        if (s.Players.Any(p => p.ForwardsHeard == 0))
        {
            return new SessionStory(
                "host-no-forward", "A player has announced itself, but no query has arrived",
                "The player has not searched yet. A read of 0 here does not mean they are blocked.",
                "Ask them to refresh Blur's LAN list.", StorySeverity.Neutral);
        }

        if (s.Counters.HostRepliesForwarded == 0)
        {
            return new SessionStory(
                "host-heard-no-reply", "The player's query arrived, but no reply has been forwarded",
                "Your Blur has not answered that query yet, so there is nothing to forward.",
                "Make sure a LAN lobby is actually open in Blur.", StorySeverity.Attention);
        }

        return new SessionStory(
            "host-forwarding", "Replies are being sent back over the overlay",
            "Your Blur has answered, and the reply has been copied to the player's overlay address.",
            string.Empty, StorySeverity.Progress);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter SessionStoryTests -c Release`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add src/BlurLink.Core/Session/SessionStory.cs tests/BlurLink.Core.Tests/SessionStoryTests.cs
git commit -m "Derive every user-visible session sentence from one tested table"
```

## Task 9: The session coordinator

**Files:**
- Create: `src/BlurLink.Core/Session/IHelperChannel.cs`
- Create: `src/BlurLink.Core/Session/SessionCoordinator.cs`
- Test: `tests/BlurLink.Core.Tests/SessionCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 7 and Task 8; `IpcMessageTypes`, `IpcSimpleCommand`, `IpcStatusResponse`, `IpcErrorResponse`.
- Produces:
  - `interface IHelperChannel { bool IsConnected { get; } Task<string> SendAsync(object message, CancellationToken ct); }`
  - `sealed class SessionCoordinator` with
    `SessionState State { get; }`,
    `event Action<SessionState>? StateChanged`,
    `Task<SessionState> RefreshAsync(CancellationToken ct)`,
    `Task<SessionState> StartAsync(object startRequest, SessionMode mode, CancellationToken ct)`,
    `Task<SessionState> StopAsync(CancellationToken ct)`,
    `SessionCapabilities Capabilities { get; init; }` and `IReadOnlyList<BlockingReason> BlockingReasons { get; init; }`.

- [ ] **Step 1: Write the failing tests**

```csharp
using BlurLink.Contracts;
using BlurLink.Core.Session;

namespace BlurLink.Core.Tests;

internal sealed class ScriptedChannel : IHelperChannel
{
    private readonly Queue<string> _responses = new();

    /// <summary>
    /// Answer used once the scripted queue is empty. This is the helper's steady
    /// state — {"active":false} by default, so a test that forgets to script a
    /// response gets"no session" rather than a misleading success.
    /// </summary>
    public string DefaultStatus { get; set; } = """{"type":"status","active":false}""";

    public List<object> Sent { get; } = new();
    public bool IsConnected { get; set; } = true;

    public void Enqueue(string json) => _responses.Enqueue(json);

    public Task<string> SendAsync(object message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : DefaultStatus);
    }
}

public sealed class SessionCoordinatorTests
{
    private static SessionCapabilities Ready =>
        new(HelperPresent: true, OverlayAddressKnown: true, AdapterSelected: true, GameRunning: true);

    [Fact]
    public async Task Refresh_ReportsRunningBridgeAndItsCounters()
    {
        var channel = new ScriptedChannel
        {
            DefaultStatus = """{"type":"status","active":true,"captured":4,"forwarded":4}""",
        };
        var sut = new SessionCoordinator(channel) { Capabilities = Ready };

        var state = await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);

        Assert.Equal(SessionMode.Bridge, state.Mode);
        Assert.Equal(SessionPhase.Running, state.Phase);
        Assert.Equal(4, state.Counters.Forwarded);
        Assert.Equal("bridge-forwarding", SessionStoryTable.Describe(state).Code);
    }

    [Fact]
    public async Task ARefusedStart_BecomesFailedWithTheHelperMessage()
    {
        var channel = new ScriptedChannel();
        channel.Enqueue("""{"type":"error","message":"another BlurLink bridge is already active on this PC"}""");
        var sut = new SessionCoordinator(channel);

        var state = await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);

        Assert.Equal(SessionPhase.Failed, state.Phase);
        Assert.Contains("already active", state.LastError);
    }

    [Fact]
    public async Task AnUnparseableStatus_BecomesFailed_NeverASilentSuccess()
    {
        var channel = new ScriptedChannel();
        var sut = new SessionCoordinator(channel);
        await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);
        channel.Enqueue("not json at all");

        var state = await sut.RefreshAsync(CancellationToken.None);

        Assert.Equal(SessionPhase.Failed, state.Phase);
        Assert.NotEmpty(state.LastError);
    }

    [Fact]
    public async Task WithoutAConnection_TheStateCarriesTheBlockingReason()
    {
        var channel = new ScriptedChannel { IsConnected = false };
        var sut = new SessionCoordinator(channel);

        var state = await sut.RefreshAsync(CancellationToken.None);

        Assert.Equal(SessionPhase.Idle, state.Phase);
        Assert.Contains(state.BlockingReasons, r => r.Code == "helper-not-running");
    }

    [Fact]
    public async Task Stop_AsksTheHelperAndReturnsToIdle()
    {
        var channel = new ScriptedChannel
        {
            DefaultStatus = """{"type":"status","active":true}""",
        };
        var sut = new SessionCoordinator(channel);
        await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);

        channel.DefaultStatus = """{"type":"status","active":false}""";
        var state = await sut.StopAsync(CancellationToken.None);

        Assert.Equal(SessionPhase.Idle, state.Phase);
        Assert.Equal(SessionMode.None, state.Mode);

        // start, the status poll that follows it, stop, then the status that confirms it.
        Assert.IsType<IpcStartRequest>(channel.Sent[0]);
        Assert.Equal(IpcMessageTypes.Stop, ((IpcSimpleCommand)channel.Sent[2]).Type);
        Assert.Equal(4, channel.Sent.Count);
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter SessionCoordinatorTests -c Release`
Expected: FAIL — `SessionCoordinator` does not exist.

- [ ] **Step 3: Implement the channel seam and the coordinator**

```csharp
// src/BlurLink.Core/Session/IHelperChannel.cs
namespace BlurLink.Core.Session;

/// <summary>
/// The one thing the coordinator needs from a helper: send a message, get the
/// JSON reply. Implemented in the app over the named pipe, faked in tests.
/// </summary>
public interface IHelperChannel
{
    bool IsConnected { get; }
    Task<string> SendAsync(object message, CancellationToken ct);
}
```

```csharp
// src/BlurLink.Core/Session/SessionCoordinator.cs
using System.Text.Json;
using BlurLink.Contracts;

namespace BlurLink.Core.Session;

/// <summary>
/// Owns the transition between helper replies and <see cref="SessionState"/>.
/// It is the only place that decides what the session is doing, so a phase can
/// never be inferred differently in two views.
/// </summary>
public sealed class SessionCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHelperChannel _channel;
    private SessionPhase _phase = SessionPhase.Idle;
    private SessionMode _mode = SessionMode.None;

    public SessionCoordinator(IHelperChannel channel) => _channel = channel;

    public SessionState State { get; private set; } = SessionState.Initial;

    public event Action<SessionState>? StateChanged;

    private SessionCapabilities _capabilities = SessionCapabilities.Unknown;

    /// <summary>Set by the app once it knows what exists on this machine.</summary>
    public SessionCapabilities Capabilities
    {
        get => _capabilities;
        set
        {
            _capabilities = value;
            BlockingReasons = ResolveBlockingReasons(value);
        }
    }

    /// <summary>Always derived from <see cref="Capabilities"/>; never set directly.</summary>
    public IReadOnlyList<BlockingReason> BlockingReasons { get; private set; } =
        ResolveBlockingReasons(SessionCapabilities.Unknown);

    /// <summary>Capability-derived reasons, evaluated once per set of capabilities.</summary>
    public static IReadOnlyList<BlockingReason> ResolveBlockingReasons(SessionCapabilities c)
    {
        var reasons = new List<BlockingReason>();
        if (!c.HelperPresent)
        {
            reasons.Add(new BlockingReason(
                "helper-missing",
                "The helper is not available on this machine.",
                "Build it with scripts/build.ps1, or use the portable build that embeds it."));
        }
        if (!c.OverlayAddressKnown)
        {
            reasons.Add(new BlockingReason(
                "overlay-address-missing",
                "Your overlay address is not set, so a host has no address to reply to.",
                "Pick your overlay adapter, or enter the address on the Join tab."));
        }
        return reasons;
    }

    public async Task<SessionState> RefreshAsync(CancellationToken ct)
    {
        if (!_channel.IsConnected)
        {
            _phase = SessionPhase.Idle;
            _mode = SessionMode.None;
            return Publish(SessionState.Initial with
            {
                BlockingReasons = BlockingReasons.Concat(new[]
                {
                    new BlockingReason(
                        "helper-not-running",
                        "The helper is not running, so there is no session.",
                        "Start a session to launch it (Windows will ask for Administrator)."),
                }).ToList(),
            });
        }

        string raw;
        try
        {
            raw = await _channel.SendAsync(new IpcSimpleCommand { Type = IpcMessageTypes.GetStatus }, ct);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        IpcStatusResponse? status;
        try
        {
            status = JsonSerializer.Deserialize<IpcStatusResponse>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fail($"The helper's status could not be parsed ({ex.Message}).");
        }

        if (status is null)
        {
            return Fail("The helper returned an empty status.");
        }

        return ApplyStatus(status);
    }

    /// <summary>
    /// Fold a status reply into the state. The poll loop and the tests both use
    /// this; it never talks to the helper itself, so the caller can pass a status
    /// it already parsed instead of paying for a second round trip.
    /// </summary>
    public SessionState ApplyStatus(IpcStatusResponse status)
    {
        _mode = status.HostActive ? SessionMode.Host
            : status.SniffActive ? SessionMode.Sniff
            : status.Active ? SessionMode.Bridge
            : SessionMode.None;

        // The helper is the authority on what is live: a session that exists is
        // Running, one that does not is Idle — regardless of what phase we were in
        // locally. A stop in flight keeps its phase until the helper confirms, so
        // the UI cannot claim to be idle while a teardown is still happening.
        if (_phase != SessionPhase.Stopping)
        {
            _phase = _mode == SessionMode.None ? SessionPhase.Idle : SessionPhase.Running;
        }

        return Publish(SessionState.FromStatus(status, _phase, BlockingReasons));
    }

    public async Task<SessionState> StartAsync(object startRequest, SessionMode mode, CancellationToken ct)
    {
        _mode = mode;
        _phase = SessionPhase.Starting;
        Publish(SessionState.Initial with { Mode = mode, Phase = _phase, BlockingReasons = BlockingReasons });

        string raw;
        try
        {
            raw = await _channel.SendAsync(startRequest, ct);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        if (IsError(raw, out var message))
        {
            return Fail(message);
        }

        _phase = SessionPhase.Running;
        return await RefreshAsync(ct);
    }

    public async Task<SessionState> StopAsync(CancellationToken ct)
    {
        _phase = SessionPhase.Stopping;
        try
        {
            await _channel.SendAsync(new IpcSimpleCommand { Type = IpcMessageTypes.Stop }, ct);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        _phase = SessionPhase.Idle;
        _mode = SessionMode.None;
        return await RefreshAsync(ct);
    }

    private static bool IsError(string raw, out string message)
    {
        message = string.Empty;
        try
        {
            if (JsonSerializer.Deserialize<IpcErrorResponse>(raw, JsonOptions) is { } error
                && string.Equals(error.Type, IpcMessageTypes.Error, StringComparison.Ordinal))
            {
                message = error.Message;
                return true;
            }
        }
        catch (JsonException)
        {
            // Not an error envelope; treated as a status below.
        }
        return false;
    }

    private SessionState Fail(string message)
    {
        _phase = SessionPhase.Failed;
        return Publish(SessionState.Initial with
        {
            Mode = _mode,
            Phase = SessionPhase.Failed,
            BlockingReasons = BlockingReasons,
            LastError = message,
        });
    }

    private SessionState Publish(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
        return state;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter SessionCoordinatorTests -c Release`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/BlurLink.Core/Session tests/BlurLink.Core.Tests/SessionCoordinatorTests.cs
git commit -m "Add the session coordinator: helper replies in, session state out"
```

## Task 10: Wire the WPF Join tab to the state model

This is the risk-reduction step the spec's R3 calls for: the model is proven
against the real app before the shell is replaced, and the tangle in
`JoinViewModel` starts shrinking.

**Files:**
- Create: `src/BlurLink.Desktop/Services/PipeHelperChannel.cs`
- Modify: `src/BlurLink.Desktop/ViewModels/JoinViewModel.cs` (status text and phase reporting only)
- Modify: `src/BlurLink.Desktop/Views/JoinView.xaml` (bind the story strings)
- Test: `tests/BlurLink.Core.Tests/JoinStoryWiringTests.cs`

**Interfaces:**
- Consumes: Task 9's `SessionCoordinator`, `IHelperChannel`; the existing `HelperIpcClient`.
- Produces: `sealed class PipeHelperChannel : IHelperChannel` wrapping `HelperIpcClient`; `JoinViewModel.Story` (`SessionStory`) and `JoinViewModel.RawCounters` (string) for the disclosure, with `Story` raised on every coordinator update.

- [ ] **Step 1: Write the failing test**

```csharp
using BlurLink.Core.Session;
using BlurLink.Desktop.ViewModels;

namespace BlurLink.Core.Tests;

public sealed class JoinStoryWiringTests
{
    [Fact]
    public void TheJoinTabShowsTheStoryNotRawCounters()
    {
        // Same seam the existing view-model tests use: a fake helper channel.
        var vm = JoinViewModel.ForTests(new ScriptedChannel());
        vm.ApplyStatusForTests(new BlurLink.Contracts.IpcStatusResponse
        {
            Active = true, Captured = 4, Forwarded = 4,
        });

        Assert.Equal("You are reaching the host", vm.Story.Headline);
        Assert.Contains("captured=4", vm.RawCounters);
    }
}
```

`JoinViewModel.ForTests` and `ApplyStatusForTests` are the test seam this task
adds: `ForTests` builds the view model over a supplied `IHelperChannel` instead
of launching a helper, and `ApplyStatusForTests` pushes one helper status through
the coordinator and raises the bindings. They are `internal` and the test project
already has access to the desktop assembly. If `JoinViewModel` has an equivalent
seam from its existing tests, use that instead and keep the assertion unchanged
— but then the seam's shape must be written into this plan before executing.

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter JoinStoryWiringTests -c Release`
Expected: FAIL — no `Story` property.

- [ ] **Step 3: Implement the channel and wire the view model**

```csharp
// src/BlurLink.Desktop/Services/PipeHelperChannel.cs
using BlurLink.Core.Session;

namespace BlurLink.Desktop.Services;

/// <summary>Adapts the named-pipe client to the coordinator's seam.</summary>
public sealed class PipeHelperChannel : IHelperChannel
{
    private readonly HelperIpcClient _client;
    public PipeHelperChannel(HelperIpcClient client) => _client = client;
    public bool IsConnected => _client.Connected;
    public Task<string> SendAsync(object message, CancellationToken ct) => _client.SendAsync(message, ct);
}
```

In `JoinViewModel`: add `private readonly SessionCoordinator _coordinator;`,
expose

```csharp
public SessionStory Story => SessionStoryTable.Describe(_coordinator.State);

public string RawCounters =>
    $"captured={_coordinator.State.Counters.Captured} forwarded={_coordinator.State.Counters.Forwarded} "
    + $"dropped={_coordinator.State.Counters.Dropped} dedupSkipped={_coordinator.State.Counters.DedupSkipped}";
```

construct the coordinator over a `PipeHelperChannel`, and in the existing status
poll replace the hand-built status sentences with `Raise(nameof(Story))` and
`Raise(nameof(RawCounters))` after the coordinator updates. Delete the replaced
sentence-building branches; do not leave both paths alive.

The two test hooks, as real members:

```csharp
/// <summary>Test seam: drive the tab from a fake helper instead of a real one.</summary>
internal static JoinViewModel ForTests(Session.IHelperChannel channel) =>
    new(new SessionCoordinator(channel) { Capabilities = ReadyForTests() });

internal static SessionCapabilities ReadyForTests() =>
    new(HelperPresent: true, OverlayAddressKnown: true, AdapterSelected: true, GameRunning: true);

/// <summary>One helper status through the coordinator, then rebind the view.</summary>
internal void ApplyStatusForTests(BlurLink.Contracts.IpcStatusResponse status)
{
    _lastStatus = status;              // already the field the poll writes into
    _coordinator.ApplyStatus(status);
    Raise(nameof(Story));
    Raise(nameof(RawCounters));
}
```

(`SessionCoordinator.ApplyStatus` is the member from Task 9 — public, not a test
hook, because the app's own poll already holds the status object it just parsed
and should not pay for a second round trip to hand it over.)

**One more line, or none of the internal seams compile:** the test project cannot
see `internal` members without being told it may. Add to
`src/BlurLink.Desktop/BlurLink.Desktop.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="BlurLink.Core.Tests" />
  </ItemGroup>
```

Verify with a build, not by reading: `dotnet build BlurLink.sln -c Release` must
fail without this line (the test project references the internal seams) and
succeed with it.

In `JoinView.xaml`, bind the three story lines plus a collapsible raw block:

```xml
<TextBlock Text="{Binding Story.Headline}" FontWeight="SemiBold" />
<TextBlock Text="{Binding Story.Detail}" TextWrapping="Wrap" />
<TextBlock Text="{Binding Story.SuggestedAction}" TextWrapping="Wrap" Opacity="0.8" />
<Expander Header="Counters">
  <TextBlock Text="{Binding RawCounters}" FontFamily="Consolas" />
</Expander>
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test BlurLink.sln -c Release --nologo`
Expected: all green — the new test plus every existing test (220 + the ones added in Tasks 7–9 + this one). Any test that asserted the old hand-built sentences must be updated to assert the story table's output, and the task's commit message must name it.

- [ ] **Step 5: Manual sanity check**

Launch the app unelevated and confirm: with no helper running, the Join tab reads
the "helper-not-running" blocking reason rather than a stale counter line; the
Counters expander shows the raw numbers. Screenshot for the PR description.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Wire the Join tab to the session state model and retire its hand-built status text"
```

## Task 11: The NativeAOT spike

Spec D10: the result is recorded, the default deliverable does not change.

**Files:**
- Create: `scripts/build-aot.ps1`
- Create: `docs/aot-spike.md`

**Interfaces:**
- Consumes: the existing portable build (`scripts/build-portable.ps1`), `HelperLauncher.EnsureStagedBinaries`.
- Produces: `scripts/build-aot.ps1` — publishes the app as a NativeAOT single file to `dist/BlurLink-Aot/`, prints the size in MB and the cold-start time in ms, and exits non-zero if the publish fails. `docs/aot-spike.md` records the numbers and the verdict.

- [ ] **Step 1: Write the script**

```powershell
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
```

- [ ] **Step 2: Run it against the real Avalonia target**

Before the Avalonia port lands, `-p:PublishAot=true` on the WPF project is
expected to **fail** — that is the recorded baseline. Run it and keep the error
in `docs/aot-spike.md` as the WPF/AOT datum (`NETSDK1168`), because it is the
reason the flavor exists at all.

- [ ] **Step 3: Write `docs/aot-spike.md`**

Table: flavor, size, cold start, whether helper staging and the UAC launch still
worked, and what failed. Explicitly state which numbers came from WPF (baseline,
expected failure) and which from Avalonia (ran after Task 4 of the surface plan).
Include the comparison against the measured slim framework-dependent 3.5 MB.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Spike a NativeAOT single-file build and record what it measures"
```

## Task 12: Release automation proven by a dry run

Spec §8.4. Unsigned, portable, hashed, attached.

**Files:**
- Create: `scripts/package-release.ps1`
- Create: `.github/workflows/release.yml`
- Modify: `docs/REPO.md` (how a release is cut)

**Interfaces:**
- Consumes: `scripts/build-portable.ps1`, `third-party/WinDivert/README.md`'s pinned hashes.
- Produces: `scripts/package-release.ps1 -Version <semver>` → `dist/release/BlurLink-<version>-win-x64.zip` containing `BlurLink.exe`, `LICENSE`, `THIRD-PARTY-NOTICES.md` and `SHA256SUMS.txt`; prints the SHA256 of the zip.

- [ ] **Step 1: Write `scripts/package-release.ps1`**

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
  Builds the portable exe and packages it for release: exe + notices + hashes,
  zipped. Unsigned on purpose (V2 decision D6) - document the SmartScreen
  warning in the release notes rather than implying it does not happen.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root 'dist/release/staging'
$releaseDir = Join-Path $root 'dist/release'

& (Join-Path $PSScriptRoot 'build-portable.ps1')
if ($LASTEXITCODE -ne 0) { throw 'portable build failed' }

$exe = Join-Path $root 'dist/BlurLink/BlurLink.exe'
if (-not (Test-Path $exe)) { throw "no portable exe at $exe" }

$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if ($fileVersion -notlike "$Version*") {
  throw "version mismatch: exe is $fileVersion, requested $Version (bump Directory.Build.props first)"
}

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item $exe $staging
Copy-Item (Join-Path $root 'LICENSE') $staging
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $staging

Push-Location $staging
try {
  $lines = Get-ChildItem -File | Where-Object Name -ne 'SHA256SUMS.txt' |
    ForEach-Object { "$((Get-FileHash -Algorithm SHA256 $_).Hash.ToLowerInvariant())  $($_.Name)" }
  $lines | Set-Content -Encoding ascii 'SHA256SUMS.txt'
} finally { Pop-Location }

$zip = Join-Path $releaseDir "BlurLink-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip

$zipHash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Write-Host "release: $zip"
Write-Host "sha256:  $zipHash"
```

- [ ] **Step 2: Write the release workflow**

```yaml
name: release

on:
  push:
    tags: ['v*']
  workflow_dispatch:
    inputs:
      version:
        description: 'Version to package (matches FileVersion)'
        required: true

jobs:
  package:
    runs-on: windows-latest
    permissions:
      contents: write
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - name: Build the helper
        run: |
          cmake -S src/BlurLink.Net -B out/native -DCMAKE_BUILD_TYPE=Release
          cmake --build out/native --config Release
      - name: Package
        shell: pwsh
        run: |
          $version = if ('${{ github.event.inputs.version }}') { '${{ github.event.inputs.version }}' } else { '${{ github.ref_name }}'.TrimStart('v') }
          scripts/package-release.ps1 -Version $version
      - uses: actions/upload-artifact@v4
        with:
          name: BlurLink-release
          path: dist/release/*
      - name: Attach to the release
        if: startsWith(github.ref, 'refs/tags/')
        uses: softprops/action-gh-release@v2
        with:
          files: dist/release/*
```

- [ ] **Step 3: Dry-run it**

Run locally: `pwsh -NoProfile -File scripts/package-release.ps1 -Version 0.2.0`
Expected: `dist/release/BlurLink-0.2.0-win-x64.zip` exists; unpack it and confirm
three files plus `SHA256SUMS.txt`, and that `BlurLink.exe` launches and stages its
helper (check `%LocalAppData%\BlurLink\bin`). Then trigger the workflow with
`workflow_dispatch` and confirm the artifact.

- [ ] **Step 4: Document and commit**

Add the "how a release is cut" section to `docs/REPO.md`: bump `FileVersion`,
run the script, tag, workflow attaches. Then:

```bash
git add -A
git commit -m "Add release packaging with hashes and prove it with a dry run"
```

---

## Verification ledger

Fill this in as tasks complete; it is the stage's honest evidence, in the style
of the V1 plan's STATUS table.

| Task | Status | Evidence to record |
|---|---|---|
| 1 | | `git log --oneline` shows the commits; `git status --porcelain` empty |
| 2 | | `scripts/check-docs-privacy.ps1` prints `privacy-ok`; the deliberate violation was caught |
| 3 | | `scripts/check-doc-claims.ps1` prints `claims-ok`; the deliberate violation was caught; both run from `scripts/test.ps1` and the CI `docs` job |
| 4 | | build clean; `dotnet test` output: 220 passed (plus added tests) |
| 5 | | `docs/driver-ci-experiment.md` carries the verdict and the raw lines; workflow artifact linked |
| 6 | | unelevated run exits 2 with `FAIL elevated`; elevated run exits 0 with `PREFLIGHT OK` |
| 7 | | `SessionStateTests` green |
| 8 | | `SessionStoryTests` green (11) |
| 9 | | `SessionCoordinatorTests` green (5) |
| 10 | | full suite green; Join tab screenshot showing a story line and the Counters expander |
| 11 | | `docs/aot-spike.md` carries size, startup and the staging/UAC result, including the WPF `NETSDK1168` baseline |
| 12 | | zip in `dist/release`, contents verified, workflow artifact produced |

Stage 0 exit criteria: tasks 1–6 done. Stage 1 exit criteria: tasks 7–12 done,
with the full suite green and the AOT verdict recorded.

## Execution handoff

**Two execution options:**

1. **Subagent-Driven (recommended)** — a fresh subagent per task, review between
   tasks, fast iteration.
2. **Inline Execution** — tasks executed in this session with checkpoints.

Which approach?
