# BlurLink

Windows-only open-source bridge that helps the 2010 PC game **Blur** discover
remote LAN lobbies through a virtual LAN/VPN overlay **you already run**.

What it is not: no VPN, no relay, no backend, no account, no matchmaking, no NAT
traversal, no tunnel, no game server, no Blur.exe modification, no payload rewriting.

V1 research history in 3 lines: the discovery port was chased through live
captures and detector runs (one early reading later withdrawn); the full record
lives in `docs/packet-research.md`. The chase is over: the port is **50001, fixed**.

## Verified

- **Join-only lobby + join over a real tunnel (author pair, 2026-09-15).**
  `captured=2 forwarded=1 dropped=0`: the lobby listed and the join completed
  with no host mode running. The duplicate forward was suppressed by design.
- **Discovery port 50001, fixed by capture + live proof.** 2026-09-12: the real
  24-byte query to `255.255.255.255:50001` and the real 160-byte answer
  (`50001 -> 50001`, host endpoints embedded) captured from a live game.
  2026-09-15: a real lobby listed and joined on that port. Empty or wrong
  ports refuse to start by design.
- **Driver-level suites green.** Managed: **300/300** (Core 234 + Shell 66,
  0 failed — verified by `dotnet test BlurLink.sln -c Release` on this commit).
  The WinDivert driver path was exercised for real, elevated, on 2026-09-12:
  the bridge filter matched live injected traffic, and the host-mode composite
  filter matched the reply, wrong-port and broadcast cases separately.

## Still open

- **(b) Which embedded endpoint the player's Blur dials — OPEN, non-blocking.**
  The host reply embeds two endpoints (physical LAN + overlay, gameplay port
  3074); which one Blur dials decides whether payload rewriting is ever needed.
  The M1 evidence session was skipped by user decision (2026-09-21), so this
  stays open and no rewriter is built until evidence says so.
- **Driver CI — OPEN (PENDING).** `.github/workflows/driver.yml` has never
  executed: the only remote is GitLab-only, so no GitHub run exists yet.
  See `docs/driver-ci-experiment.md`.
- **Elevated end-to-end assertions — OPEN.** `scripts/test-e2e.ps1` (42 checks)
  needs elevation + the driver; re-verification waits for the next elevated run.
- **Tray headed eyeball — DONE (Task 2).** Unelevated launch, PIDs 17484/13820,
  window title `BlurLink`, build 0.3.0: tray icon visible, tooltip mirrors the
  story, Show + Copy diagnostics + Quit paths exercised, no crash on Quit.

## Install

1. Take the slim exe (framework-dependent single file, ~32 MB at 0.3.0 —
   Avalonia + Skia natives + embedded helper/driver):
   `dist/BlurLink/BlurLink.exe`. It needs the
   [.NET 10 Desktop Runtime](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe).
    The same installer is attached to each GitHub release next to the app zip.
2. The helper (`blurlink-net.exe`) and the WinDivert runtime ride inside the exe
   and are staged to `%LocalAppData%\BlurLink\bin\` on first bridge start —
   no manual driver setup (see `third-party/WinDivert/README.md`).
3. The build is unsigned, so Windows SmartScreen warns on first launch.

## Use

1. Start your VPN/LAN emulator first, where you can already ping the host
   overlay IP by hand (e.g. host `100.96.47.177`).
2. Open BlurLink → **Join**: enter the host's overlay IPv4, select *your*
   overlay adapter, press **Start Bridge** and approve the UAC prompt (helper only).
3. The story banner tells you the state; the discovery port shows fixed 50001,
   broadcast fixed `255.255.255.255`, payload prefix empty — nothing to type.
4. Launch Blur, open its LAN games list; the `forwarded` counter ticks as Blur
   broadcasts. **Stop Bridge** when done. Everything else lives under Advanced.

## Host fallback (Advanced note)

When: strict NAT, mobile hotspots, university WiFi, or `.255` edges — the helper
host shows `forwards heard > 0` but nothing arrives on the joiner, because the
host's Blur answers the joiner's physical LAN address, which cannot route back
over the overlay. The host engine stays compiled and tested with zero idle cost;
the main UI hides it. Default flows never need it — see `docs/user-guide.md`
(Advanced) and `docs/troubleshooting.md` ("Captured/forwarded but no lobby").

## Size

| Flavor | Size | Needs | Cold start |
| ------ | ---- | ----- | ---------- |
| Slim (the deliverable) | ~32 MB (app + Skia natives + embedded helper/driver) | .NET 10 Desktop Runtime | instant |
| Full, legacy (`-Full`) | ~140 MB | nothing | instant |
| SFX, legacy (`-Full -Sfx`) | ~43 MB | nothing | +~2s extract, one prompt |

`-Full`/`-Sfx` stay because AOT failed on both shells (WPF `NETSDK1168`; Shell
16x `IL2026`/`IL3050` at Core JSON sites). Full record: `docs/aot-spike.md`.

## Support

- Diagnostics → **Create support bundle** (logs + diagnostics metadata, zipped;
  payloads never included).
- App log: `%LocalAppData%\BlurLink\logs\blurlink.log`. Helper log:
  `%LocalAppData%\BlurLink\logs\helper.log` (rotates at 1 MB to `helper.log.1`).
- Quote the story-banner state, both counters, and recent log lines when asking
  for help.
