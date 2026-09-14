# Capture-day checklist (for you + a friend)

Goal: produce the first verified Blur discovery profile. You need two PCs
on the **same physical LAN you own** (or are authorized to test), Blur
installed on both, BlurLink **disabled** for the baseline.

## Before the session

- [ ] Both PCs: note Windows build (`winver`), Blur build/version, LAN
      adapter names + IPv4 addresses.
- [ ] Friend (host): can create a Blur LAN lobby. You (joiner): can see it
      and join it with BlurLink disabled. If the baseline doesn't work,
      stop — BlurLink can't fix the game itself.
- [ ] Joiner: install Wireshark. Do a 10-second test capture on the LAN
      adapter to confirm capturing works.

## Baseline capture (BlurLink off)

1. Host: create the LAN lobby, note the exact lobby name + time.
2. Joiner: start Wireshark on the LAN adapter, capture filter `udp`.
3. Joiner: open Blur's LAN browser / refresh. Wait 10s. Stop capture, save
   as `blur-baseline-<date>.pcapng` (keep it private — your LAN).
4. Repeat the refresh twice more (`-refresh2`, `-refresh3`) for stability
   evidence.

## What to read from the capture

- [ ] Display filter `udp.dstport == X and (ip.dst == 255.255.255.255)`:
      the joiner's discovery requests. Record **UDP dst port**,
      **broadcast dst**, **source port** (same every time?).
- [ ] Find the host's answer (filter the host's IP). Record direction,
      ports, and approximate round-trip time.
- [ ] **Embedded-IP check:** inspect the response payload bytes — does the
      host's physical LAN address (e.g. 192.168.x.y) appear in it? Note the
      verdict (present/absent). Do not edit anything.
- [ ] **Signature check:** compare the first 8–16 payload bytes of the
      request across all three captures. Identical → candidate
      `payloadPrefixHex`. Different → leave the signature empty.

## First live bridge test

1. Both: start your VPN/LAN emulator, confirm you can ping each other's
   overlay IPs (BlurLink Settings → Ping host).
2. Joiner: enter host overlay IP + verified port/broadcast (+ signature if
   stable), select overlay adapter, confirm the pre-flight line is all ✓
   and the route line matches the adapter.
3. Host: lobby open. Joiner: Start Bridge → approve UAC → refresh Blur's
   LAN list.
4. Report back: `captured`/`forwarded` counts, lobby visible? (Y/N), join
   works? (Y/N), "Copy diagnostics" output, and `%LocalAppData%\BlurLink\
   logs\blurlink.log`.

## If it fails, capture (don't guess)

- No `captured`: wrong port/broadcast/signature — re-check the baseline.
- `forwarded` but no lobby: capture on the *overlay* adapter (filter
  `udp port <discoveryPort>`) — did the unicast arrive? Did the host
  answer? Firewall?
- Lobby visible, join fails: almost certainly the embedded-LAN-IP case —
  record the capture evidence for a future verified profile.

## Appendix: scripted IPC interop (no Blur needed)

`scripts/test-interop.ps1` drives a locally built `blurlink-net.exe`
through `get_status` → bad-token rejection → `start` (expects a clean
driver error without WinDivert) → `shutdown`, plus a watchdog test.
Run it after every helper change:
`pwsh scripts/test-interop.ps1 -HelperExe out\interop\blurlink-net.exe`
