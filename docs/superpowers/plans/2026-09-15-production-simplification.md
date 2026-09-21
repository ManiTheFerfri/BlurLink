# Production Simplification Plan (post live-lobby verdict)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the research shell into a stable production app: discovery port hardcoded to 50001, Host out of the main UI (engine retained), Join-only modern dark UI with research extras removed.

**Architecture:** Shell-only changes (+Contracts/Core defaults + profile + claims registry). WPF frozen except nothing (it is not touched by any task here). Native helper untouched (host session stays, unused by default). No new packages, no new projects.

**Tech Stack:** .NET 10 LTS (`net10.0-windows`), Avalonia 12.1.2, xUnit (v2 Core / v3 Shell), PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-09-12-v2-design.md` (evidence now extended by the 2026-09-15 live lobby: join-only, no host mode, `captured=2 forwarded=1 dropped=0`, lobby + join OK).

## Global Constraints

- .NET 10 LTS floor; Windows-only; no other OS work.
- Zero new runtime dependencies. No DI container, no MVVM toolkit, no webview.
- No payload rewriting; no telemetry/accounts/backend/listener weakening; metadata-only diagnostics; synthetic fixtures only, never real capture bytes.
- WPF project frozen: no WPF file touched by any task (M4 owns WPF removal).
- Native helper: no change (host engine stays compiled and tested, idle cost zero).
- Honesty rules: exit 2 never a pass; PENDING labeled never faked; no launches/screenshots/elevated runs claimed without evidence.
- Test baseline at plan time: 306 managed (Core 276 + Shell 30). Keep green; name every updated assertion in the commit message.

## Evidence note (why 50001 is now hardcoded)

Three independent legs: (1) 2026-09-12 captures — real 24-byte query to `255.255.255.255:50001`, real 160-byte answer `50001->50001`; (2) detector runs naming 50001 top port repeatedly; (3) 2026-09-15 live lobby over the tunnel, join-only, `captured=2 forwarded=1` (duplicate suppressed by design), lobby listed + join completed. Research mode gate is retired by evidence, not enthusiasm.

---

## Task A: Hardcode discovery port 50001

**Files:**
- Modify: `src/BlurLink.Contracts/BlurLinkConfig.cs`, `src/BlurLink.Contracts/GameProfile.cs`, `src/BlurLink.Contracts/Constants.cs` (add `DiscoveryUdpPortDefault = 50001` with evidence comment)
- Modify: `src/BlurLink.Core/Config/BlurLinkConfigStore.cs` (`Migrate`: null/0 -> 50001), `profiles/research-mode.json` (port 50001 + notes rewritten, file kept for history)
- Modify: `src/BlurLink.Shell/ViewModels/BridgeSettingsViewModel.cs` (init + RefreshFromConfig default `"50001"`), `JoinSessionViewModel.cs`, `HostViewModel.cs` (same two spots), `FirstRunViewModel.cs` (port step auto-done, fixed display), `MainViewModel.cs` (`ShowFirstRun`: drop the `DiscoveryUdpPort is null` clause -> badge/dismiss only), `VerifyProfileViewModel.cs` (prefill port display from default)
- Modify: `docs/claims-registry.json` (ADD grounded entry: discovery port 50001, inCode Contracts/Constants.cs, mustAppearIn Shell Join view or user-guide — grounded by grep first), `docs/user-guide.md` + `docs/troubleshooting.md` (replace research-mode-first prose with fixed-port prose)
- Test: update every test asserting empty-port refusal or null default (expect: `JoinViewModelStartFlowTests`, `BridgeSettings`-adjacent, `ConfigMigrationTests` allow-list, `FirstRunTests` badge/step tests, `PrefixStability`-adjacent if any). Name each in the commit message.

**Interfaces:**
- Consumes: nothing new.
- Produces: `BlurLinkConstants.DiscoveryUdpPortDefault == 50001`; config default 50001 everywhere; checkers green on the new registry entry.

- [ ] **Step 1: Add the constant + config defaults**
```csharp
// Contracts/Constants.cs
/// <summary>Discovery port, verified by capture (2026-09-12 real query/answer)
// + live lobby (2026-09-15, join-only). Hardcoded by evidence; override lives
// only in Advanced manual entry, never in the default flow.</summary>
public const int DiscoveryUdpPortDefault = 50001;
```
```csharp
// Contracts/BlurLinkConfig.cs — replace the null-default property initializer:
public int? DiscoveryUdpPort { get; set; } = BlurLinkConstants.DiscoveryUdpPortDefault;
```
`BlurLinkConfigStore.Migrate` gains: `if (cfg.DiscoveryUdpPort is null or 0) cfg.DiscoveryUdpPort = BlurLinkConstants.DiscoveryUdpPortDefault;`
`GameProfile.ResearchMode()` stays (compat) but default `DiscoveryUdpPort = BlurLinkConstants.DiscoveryUdpPortDefault`; `research-mode.json` port -> `50001`, notes -> fixed-port sentence.

- [ ] **Step 2: VMs default to the constant**
Every `config.DiscoveryUdpPort?.ToString() ?? string.Empty` in Shell VMs (BridgeSettings x2, Host x2, FirstRun/Verify writers) becomes `?.ToString() ?? DiscoveryUdpPortDefault.ToString()`. `ValidateInputs`/preflight keep validating (wrong values still refused) but empty is now impossible in the default flow. Join/Session `StartAsync` paths that wrote `_config.DiscoveryUdpPort = port` stay (they persist the effective port, now 50001 unless Advanced overrides).
`MainViewModel.ShowFirstRun` drops `&& Config.DiscoveryUdpPort is null`. FirstRun port step: detail reads `"Discovery port is fixed at 50001 (verified)."` and is always Done.

- [ ] **Step 3: Registry + docs**
Grep `50001` in `src/BlurLink.Contracts/Constants.cs` (must hit the new constant) and in the chosen doc (must hit `50001` prose); only then add the registry entry. Rewrite research-first sentences to fixed-port sentences; keep the manual-override note in Advanced.

- [ ] **Step 4: Run gates**
`dotnet build BlurLink.sln -c Release` (0/0), full suite green, both checkers green. Every updated test named.

- [ ] **Step 5: Commit**
```bash
git add src/BlurLink.Contracts src/BlurLink.Core/Config profiles/research-mode.json src/BlurLink.Shell docs/claims-registry.json docs/user-guide.md docs/troubleshooting.md tests
git commit -m "Hardcode discovery port 50001 by evidence; retire research-mode gate (tests: ...)"
```

## Task B: Host out of the main UI, engine retained

Why retained (tell the user plainly): this pair routes back without it (same /16 overlay, Windows chose an overlay source), but a future pair on strict NAT / mobile / university WiFi / a /23 `.255` edge can still hit reply-dies-at-host — the only fix is the tested host engine. Idle cost is zero (no handle, no traffic); deleting tested code to re-add later costs a retest. So: UI hides it, code stays.

**Files:**
- Modify: `src/BlurLink.Shell/Views/MainWindow.axaml` (remove Host nav button), `src/BlurLink.Shell/ViewModels/MainViewModel.cs` (`CurrentPane`: remove `"Host" => Host` so Host is unreachable; KEEP the `Host` property + construction + fan-out + Dispose — zero UI cost, tests keep driving it), `src/BlurLink.Shell/App.axaml` (keep Host DataTemplate — harmless, headless tests construct HostView directly)
- Modify: `docs/user-guide.md` (Host section -> `Advanced (rarely needed)` note), `docs/troubleshooting.md` (host branch stays as the fallback)
- Test: navigation test `Navigation_ReachesAllFourDestinations` -> three destinations (Join/Settings/Diagnostics); name the change. Host headless tests stay (engine proven, still compiled).

- [ ] **Step 1: Hide nav, keep the engine**
Remove the Host `<Button>` block from `MainWindow.axaml` and the `"Host" => Host,` line from `CurrentPane`. Nothing else: no VM deletion, no native change, no WPF touch.
- [ ] **Step 2: Docs to Advanced**
User-guide Host section becomes a 5-line Advanced note (when to use: `forwards heard > 0` on a helper host but nothing arrives; what it does; that default flows never need it). Troubleshooting host branch stays verbatim.
- [ ] **Step 3: Gates** — build 0/0, full suite green, checkers green. Name the updated navigation test.
- [ ] **Step 4: Commit** — `git add src/BlurLink.Shell docs tests` + message naming the navigation change + the retain rationale in one line.

## Task C: Join-only modern dark UI (research extras removed)

Stays: Host IP field, Adapter picker, big Join/Stop button, story banner (headline/detail/action), Verified badge (now static `Verified 50001` once Task A lands), route line, footer Copy diagnostics, Blur launch row, Advanced expander (route details, raw counters, recent events). Removed from the VIEW (VMs stay compiled + tested): sniff Detect/Listen/Apply section, payload-prefix field (always empty), broadcast edit (fixed display `255.255.255.255`), Verify-profile section (rule + tests stay, UI goes), ResearchSteps text (reword to fixed-port sentence), FirstRun port action (auto-done display). Tokens already dark-modern (no color hardcodes, Inter font, radius 12 cards, full-width primary) — tighten spacing only, no new styles, no new deps.

**Files:**
- Modify: `src/BlurLink.Shell/Views/JoinView.axaml` (sections per above), `SettingsView.axaml` (drop research text), `DiagnosticsView.axaml` (drop Verify section), `FirstRunView.axaml` (port step display), `MainWindow.axaml` (single Join-first layout holds)
- Test: update headless tests querying removed names (remove those asserts, keep story/preflight/dismiss coverage); audit stays green (fewer controls); name each updated test.

- [ ] **Step 1: Strip the views per the list; grep-compare bindings** (every remaining `{Binding}` must resolve; removed names must have zero test references after Step 2).
- [ ] **Step 2: Update headless tests to the new surface** (same bodies where controls survive; delete asserts on removed controls — never weaken a surviving assertion).
- [ ] **Step 3: Headed sanity** — unelevated launch: Join-only, Esc, focus ring, story + badge correct, Advanced expander contents. Record, no screenshot claims.
- [ ] **Step 4: Commit** naming every updated test + what left the view vs what stayed in code.

## Task D: Verification + version bump + sign-off

- [ ] **Step 1: Bump 0.2.0 -> 0.3.0** (`Directory.Build.props` + both Shell/Desktop `<Version>`; behavior change = hardcoded port + simplified surface). Re-verify FileVersion gate via `package-release.ps1 -Version 0.3.0` dry-run rules (zip exists, 4 entries, hash, launch + staging; `dist/` never staged).
- [ ] **Step 2: Full gates** — build 0/0, suite all green, both checkers, `test.ps1` end to end, headed Join-only sanity.
- [ ] **Step 3: Commit + report** — version + verification outputs + parity-vs-WPF note (Shell Join-only vs WPF frozen) in the message.

## Verification ledger

| Task | Status | Evidence |
|---|---|---|
| A | | suite green; checkers green on new 50001 entry; no null-port path in default flow |
| B | | nav has no Host; Host tests still green; docs Advanced note |
| C | | headless updated tests green; audit green; headed sanity recorded |
| D | | 0.3.0 gate; full gates green; sign-off |
