# BlurLink

Windows-only open-source bridge that helps the 2010 PC game **Blur** discover
remote LAN lobbies through a virtual LAN/VPN overlay **you already run**.

> **Status: research/MVP.** The discovery port is **not** unknown any more: the
> real request and the real answer have both been captured from a live game
> (port `50001`, `255.255.255.255`, from and to the discovery port), so
> `docs/packet-research.md` now records verified bytes rather than a guess. What
> is still missing is a **protocol profile** — a complete, checked format — so the
> default profile remains `profiles/research-mode.json` and the tool still
> refuses to start Join mode until you enter verified values from an authorized
> local capture.

**What it does** — two halves of one round trip, each optional:

- **Join mode** translates a joiner's outgoing Blur LAN-discovery broadcast
  into a UDP unicast copy sent to the host's overlay IP (e.g.
  `100.96.47.177`). The host's unmodified Blur receives it.
- **Host mode** (the host machine's half) sends a *copy* of each of the host's
  Blur replies to the matching player's overlay address, because the host's
  Blur answers the joiner's physical LAN address, which it cannot route back
  over the overlay. Players introduce themselves (UDP 47811) and only each
  player's own replies are copied to them; the original is always left intact.

**What it does NOT do:** no VPN, no relay, no backend, no account, no
matchmaking, no NAT traversal, no tunnel, no game server, no Blur.exe
modification.

**Verification state:** 220 managed tests + portable C++ suite (92,870 checks)
+ 45 scripted live helper checks (`scripts/test-interop.ps1`: pipe auth,
protocol hello/version gate, validation paths, bridge-session exclusivity,
host-mode lifecycle and refusals, watchdog, shutdown, log-file mirror) all pass.
The WinDivert driver path is exercised for real, elevated (see below).

**Verified against the real game on 2026-09-12** (one machine, real WinDivert,
real Blur): a searching Blur broadcasts, and so does a Blur parked on its LAN
lobby screen — an earlier "a hosting Blur is silent" reading came from a 20 s
sample and is **withdrawn** (`docs/packet-research.md`); the real
24-byte discovery query was captured; an injected inbound packet was proven to
reach a listening socket (a link never tested before); and replaying that query
at the hosting Blur produced its **real 160-byte answer** — unicast, from the
discovery port to the querying port, with the host's own endpoints embedded in
the payload. Host mode's two load-bearing assumptions are therefore confirmed
against real game code rather than inferred. Byte offsets and raw hex:
`docs/packet-research.md`. **What is still unproven is the tunnel half** — whether
a player's forward crosses the overlay at all, and whether the lobby appears —
which needs two machines (`docs/capture-day-checklist.md`).

Both WinDivert sessions were exercised for real, on one machine, on 2026-09-12:
`scripts/test-e2e.ps1` passes **all 42 checks**. Phase 1 is host mode — a reply
cloned to the player's overlay address with the original still reinjected, the
wrong-port and broadcast refusal paths, the player roster and filter rebuilds.
Phase 2 is the Join-mode bridge with a live handle — a Blur-shaped LAN
discovery broadcast captured and cloned to the host's overlay address, the join
side introducing itself to the host, the payload-prefix gate, the echo
backstop, the rate limiter, the cross-process session lock (a second helper is
refused while the bridge is live and admitted once it stops) and a clean stop
that leaves no helper behind. That cannot be done with a loopback test —
WinDivert captures loopback as *outbound only*, and host mode's inputs are
inbound-only filter terms — so the harness, driven by the test-only injector in
`tools/hostsim/`, fakes the other end of the wire by injecting into the real
inbound path (and, for the bridge, the outbound path). It needs an elevated
shell and exits `2` ("cannot run", never a pass) when it does not get one.

That same harness found a **serious bug** in the shipping code: the WinDivert
shutdown constant was recorded as an ordinal when it is a bit flag, so
`Stop()` could never stop — the `stop` command never replied, and the
watchdog's own exit path hung on the same call, leaving a helper that only Task
Manager could kill. Fixed and **re-verified at the driver level**; the values
are now pinned by `static_assert` so it cannot silently return. See `TODO.md`.
Running the bridge for real found a **second shipping bug**, and only a live
handle could: four separate "we decided not to forward this" branches in the
capture loop called the reinject helper while holding the session mutex, and
that helper takes the same lock. A non-recursive `std::mutex` acquired twice on
one thread deadlocks — silently and permanently, since the command loop needs
the same lock and could not even answer `stop`. The first elevated bridge run
hung on the payload-prefix path; the rate-limiter, echo-backstop and
clone-failure paths had the identical flaw. Fixed at all four sites and
re-verified with the driver.

A two-machine session is still the authority on the real thing — what is proven
here is BlurLink's own logic against the real driver on both sides, not a real
Blur lobby.

```
[BlurLink GUI (non-elevated)] --named pipe--> [blurlink-net.exe (elevated, WinDivert)]
  Join side: joiner's Blur broadcast ──copy──▶ host overlay IP ──▶ host's Blur (unmodified)
  Host side: host's Blur reply        ──copy──▶ player overlay IP ──▶ joiner's Blur (unmodified)
```

## Repo layout

```
/BlurLink.sln
/src/BlurLink.Desktop    WPF MVVM GUI (non-elevated)
/src/BlurLink.Contracts  IPC + config schema
/src/BlurLink.Core       validation, filter, adapters, routing, diagnostics
/src/BlurLink.Net        C++20 WinDivert helper (elevated, local pipe only)
/tests/BlurLink.Core.Tests
/tests/BlurLink.Net.Tests
/tools/hostsim          test-only packet injector (never shipped; see below)
/docs  architecture, security, packet-research, user-guide, troubleshooting
/scripts  build.ps1, test.ps1, package.ps1, build-portable.ps1, test-interop.ps1,
          test-e2e.ps1
/profiles  research-mode.json
/third-party/WinDivert  README (binaries not committed)
```

## Build

Requirements: .NET 10 SDK, VS2022 (v143) or CMake for the helper, Windows
10/11 x64, WinDivert 2.x x64 staged per `third-party/WinDivert/README.md`.

```powershell
scripts/build.ps1     # managed + native
scripts/test.ps1      # dotnet test + native ctest
scripts/package.ps1   # dev folder under dist/ (unsigned — SmartScreen warns)
scripts/build-portable.ps1  # ONE BlurLink.exe (helper embedded) under dist/BlurLink-Portable/
scripts/test-interop.ps1    # 45 live helper checks (no admin needed)
scripts/test-e2e.ps1        # host mode + Join-mode bridge end-to-end, 42 checks
                            # (needs ELEVATION + the driver; -Phase host|bridge;
                            #  refuses to run while Blur is up, -AllowLiveBlur overrides)
```

Portable: `dist/BlurLink/BlurLink.exe` is the whole app in one file
(~3.5 MB, needs the .NET 10 Desktop Runtime:
https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe).
The helper rides inside it and is
extracted to `%LocalAppData%\BlurLink\bin\` on first bridge start. Only
WinDivert stays external — put `WinDivert.dll` + `WinDivert64.sys` in that
same `bin` folder (see `third-party/WinDivert/README.md`).

> PROJECT DECISION (2026-09-09): slim-only. We ship just the slim
> framework-dependent exe. `build-portable.ps1 -Full` keeps the legacy
> self-contained build (~140 MB) for emergencies only — do not build it
> by default.

## Size

| Flavor | Size | Needs | Cold start |
| ------ | ---- | ----- | ---------- |
| Slim (the deliverable) | ~3.5 MB | .NET 10 Desktop Runtime | instant |
| Full, legacy (`-Full`) | ~140 MB | nothing | instant |
| SFX, legacy (`-Full -Sfx`) | ~43 MB | nothing | +~2s extract, one prompt |

~99% of the portable is the .NET + WPF runtime, not our code (measured
breakdown: PresentationFramework 15 MB, WinForms present-but-unused 21 MB,
CoreLib 13 MB, …). Investigated and rejected: trimming (SDK hard-blocks
WPF, NETSDK1168), invariant globalization (WPF data binding requires real
cultures — proven by crash), deleting framework files (unsupported, crash
risk), UPX (AV red flag for a network tool). Kept: English-only satellites
(~24 MB saved, launch-verified). If sharing, SFX is the sweet spot; for
daily use, take portable (or slim if the runtime is installed).

Screenshots: *placeholder — add `docs/img/host.png`, `docs/img/join.png`.*

## Security notes

- Narrow WinDivert filter only: Join mode matches one port + one broadcast
  address outbound; Host mode matches the introduction port plus traffic to
  and from the accepted players' LAN addresses.
- Host mode auto-accepts unauthenticated player introductions, so anyone on
  the host's overlay network can be sent copies of discovery replies
  addressed to them — never general traffic, and the original is always left
  intact. Bounds and rationale: `SECURITY.md` → "Accepted trade-offs".
- GUI never elevated; helper elevates via UAC only while bridging or
  hosting.
- One GUI instance and one active bridge session at a time: a named-event
  guard stops a second window, and the helper refuses a concurrent bridge
  session even if a copy bypasses the guard.
- Local ACL'd named pipe + per-launch token; no network listener.
- No telemetry; no payload logging; diagnostics are metadata-only.
- Details: `docs/security.md`, `THIRD-PARTY-NOTICES.md`.

## Limitations (v1)

Research mode by default; IPv4 only; one broadcast dst per run; no payload
rewrite (see `TODO.md` for the verified-profile path).
