# BlurLink troubleshooting

## Cannot reach host overlay IP

- Ping the host overlay IP from Settings (explicit button). No reply →
  your VPN/LAN emulator isn't connecting you. Fix that first; BlurLink
  cannot help until ICMP (or any traffic) flows.
- Both sides: is the emulator running, logged in, same network?

## Wrong adapter route

- Symptom: route warning on the Join tab (`route uses ifIndex X, not your
  selected adapter`).
- Cause: Windows sends overlay traffic via another adapter (typical when
  the emulator's route metric loses to Wi-Fi/Ethernet).
- v1 does not alter routes. Reorder adapter metrics in Windows settings or
  fix the emulator's routing, then re-run the route test.

## No captured discovery packets (`captured=0`)

- Did you enter the verified discovery port? Use Join → Advanced →
  **Detect discovery port** while refreshing Blur's LAN list instead of
  guessing — Research mode (empty port) refuses to start.
- Is Blur actually broadcasting? Refresh its LAN list while watching the
  counters. Wrong broadcast destination (global vs directed vs multicast)
  also yields zero.
- **Give it longer than you think.** Blur's discovery query is *bursty*, not on a
  steady timer: measured on 2026-09-12, two queries 8.9 s apart, then **31.7 s of
  complete silence** in the same 45 s window. A short watch can sit entirely
  inside a gap while the game is broadcasting perfectly well. That is exactly how
  a 20 s sample once produced a wrong "the host never broadcasts" conclusion, so
  prefer a timestamped run (join → Advanced, or `hostsim capture --timestamps`)
  over a quick look, and resist concluding anything from a handful of seconds.
- A payload signature that doesn't match silently filters everything:
  clear it and retry.

## Detector hears nothing

- Refresh Blur's LAN list *during* the 15s listen, not before/after.
- The detector listens on both `255.255.255.255` and your adapter's
  directed broadcast; detection ignores signatures by design.

## Captured/forwarded but no lobby

Decide where it dies, in order:

1. **Can you even reach the host?** Settings → Ping host. No reply →
   VPN problem, stop here.
2. **Is the host lobby open and allowed?** Host: LAN lobby actually open
   in Blur; Windows Firewall allowed Blur (check on first launch).
3. **Does anything answer?** Join → Advanced → **Listen for replies**
   (needs the bridge running + the discovery port set), then refresh
   Blur's LAN list:
   - *Replies from your host IP arrive* but no lobby → Blur ignored the
     answer (likely an embedded LAN IP — capture evidence, see below).
   - *The host's Blur replied, but the reply never left the host* → the
     host is answering your **physical LAN address**, which it cannot
     route back over the overlay, so the reply dies on the host machine.
     This is the expected shape on a genuinely remote overlay, not a
     misconfiguration on your side. Look for it when the host's Host tab
     shows **forwards heard** going up (the host *is* receiving you) while
     you receive nothing. Fix: the host runs **Host mode** (Host tab →
     Start Host Mode), which sends a copy of each reply to your overlay
     address and leaves their own LAN untouched. The check on the host is
     `forwards heard > 0` — if that is **0**, the forward never reached
     the host, Host mode cannot help, and this branch does not apply.
   - *Nothing arrives* → the host never answered: host firewall, the
     lobby is genuinely closed, or the wrong host IP.
   - *Something else answers* → wrong host address; double-check it.

## Lobby visible but join fails

- Likely the host response embeds its physical LAN IP and Blur dials that
  instead of the overlay IP. Confirm via capture (see packet-research step
  5). v1 deliberately has no payload rewrite; record the evidence for a
  future verified profile instead of hand-editing bytes.

## Elevation / driver errors

- UAC cancelled → bridge not started (by design). Start again to retry.
- `WinDivert.dll not found` → follow `third-party/WinDivert/README.md`.
- Access denied → helper must be elevated; don't run the whole GUI as
  admin, just approve the helper prompt.
- x86/ARM Windows → v1 supports Windows 10/11 x64 only.

## "Lost connection to the helper"

- The GUI shows this after 3 failed polls. The helper's watchdog should
  exit it within ~15s. If counters move or the process lingers, use
  **Force kill helper** on the Join tab, then check
  `%LocalAppData%\BlurLink\logs\blurlink.log` for the last error.

## Manual test checklist (integration)

1. Two PCs, same physical LAN, host + join works with BlurLink disabled.
2. Capture the discovery port; configure Research mode → verified profile.
3. Join bridge on; LAN refresh produces `forwarded` ticks.
4. Host receives discovery; lobby becomes visible.
5. Join works if the response embeds no non-routable LAN IP.
6. If visible-but-join-fails: capture, document, no auto-rewrite.
7. Bridge stops instantly on Stop (counters freeze, no new events).
8. No unrelated UDP captured (filter line shows the narrow filter).
9. No payloads in logs/exports (grep `%LocalAppData%\BlurLink`).
10. Route-mismatch warning triggers with a deliberately wrong adapter.
