# BlurLink security model

## Threat model

- **Untrusted input**: host IP, port, broadcast, hex signature typed by the
  user. All are strictly parsed; only canonical numeric forms reach the
  WinDivert filter. There is no string-concatenation path for filter
  injection (tested in `WinDivertFilterBuilderTests` / native `TestFilter`).
- **Local IPC**: the pipe name is random per launch, the DACL grants access
  to the current user + Administrators + SYSTEM only, every command carries
  a 256-bit capability token compared in constant time. A wrong token gets
  an error, never an action. No network listener exists.
- **Elevation**: only `blurlink-net.exe` elevates (UAC `runas`), only while
  the bridge runs. Cancelling UAC = bridge not started; the helper exits.
  The GUI never elevates and never stores credentials.
- **Driver**: WinDivert's kernel driver is a third-party attack surface we
  inherit. Mitigations: official 2.x x64 binaries only (see
  `third-party/WinDivert/README.md`), narrowest possible filter, handle
  lifetime tied to the bridge session, immediate close on stop/exit.

## Why narrow filters and local-only IPC matter

A broad filter (`udp`, `true`) would expose all of the user's UDP traffic
to diversion; a network IPC listener would expose bridge control to the
LAN. BlurLink does neither by construction: Join mode's filter matches one
port + one broadcast address outbound, Host mode's matches only the
introduction port plus traffic to/from the accepted players' LAN addresses,
and control stays on an ACL'd local pipe. The research listener is even
stricter in effect: SNIFF mode observes without diverting, blocking, or
injecting anything, for at most 60s.

## Host mode: the unauthenticated introduction

Host mode is the one place BlurLink acts on something it did not initiate, so
its trust rules are worth stating explicitly.

- **The introduction packet is unauthenticated by design** (no accounts, no
  cryptography, no server to check against). Anyone who can reach the host's
  overlay address on UDP 47811 can claim to be a player. Host mode
  **auto-accepts**, which is a deliberate convenience trade-off: per-player
  confirmation was rejected as too tedious across sessions.
- **What an uninvited announce can and cannot get.** It can cause the host to
  send copies of Blur discovery replies *addressed to the pair the announce
  claimed*. It cannot see another player's replies, cannot reach the host's
  general traffic, and cannot change what the host's own LAN sees — the
  original reply is always reinjected byte-for-byte. The filter is built from
  accepted players only, so this bound is structural, not best-effort.
- **Bounded blast radius, visible and revocable.** The roster is capped at 8
  and players expire after 45s of silence; the Host tab lists every accepted
  player with per-player **Revoke**; the counters (including refusals:
  broadcast, ambiguous, unmatched) are reported rather than silent.
- **No packet contents, ever.** Introductions carry addresses and a port and
  are parsed strictly; like the rest of the app they can never carry game or
  lobby payloads, and the logging API still has no payload parameter.
- **The player's LAN address is already known to the host** — it is the source
  address of the forwarded discovery packet. The introduction's new
  information is the player's overlay address, disclosed only to the host the
  player chose to send it to.

## No backend / no telemetry

There is no network call home, no analytics, no account. `settings.json`
holds only the documented schema (paths, IPs, ports, hex prefix). Packet
payloads are never written to disk or logs; the GUI table and the
`settings.json`/diagnostics exports contain metadata (IPs/ports/sizes)
only. A developer-only JSONL toggle is intentionally **not** shipped in v1
to keep this guarantee simple; capture with Wireshark instead.

## Admin / elevation implications

Elevation grants the helper the ability to divert packets matching its
filter and to install/use the WinDivert driver. Scope is bounded by the
validated filter + user-controlled start/stop. The app never asks you to
disable Firewall/AV, never adds firewall rules, and never persists.

## Safe handling of WinDivert

- `WinDivert.dll`/`WinDivert64.sys` are loaded from the helper directory,
  never from downloads folders or PATH surprises first.
- Unsigned development builds trigger SmartScreen/UAC warnings; that is
  expected until binaries are code-signed. Prefer building from source.
- Uninstall: delete the app folder; the WinDivert driver unloads when the
  last handle closes (reboot clears any residue). No service is installed.
