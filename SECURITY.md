# Security policy

BlurLink is a local network-bridging tool that runs an elevated packet
helper. Security reports are welcome.

## Supported versions

| Version | Supported |
| ------- | --------- |
| 0.1.x (research/MVP) | Yes |

## Reporting a vulnerability

Open a **private** report via the repository's Security Advisories tab
(preferred), or email the maintainer listed in the repo. Please include:

- BlurLink version + Windows build (`winver`)
- WinDivert version staged (if Join mode is involved)
- Steps to reproduce, ideally with `%LocalAppData%\BlurLink\logs\blurlink.log`
  (metadata only — never include packet captures from networks you don't own)

We will acknowledge within 7 days and aim to fix critical local-privilege or
traffic-exposure issues within 30 days.

## Scope notes

- The elevated helper only exists while the bridge runs and exits by
  watchdog (15s default) if the GUI disconnects.
- `settings.json` and logs contain metadata only — a report showing packet
  *payloads* in either is automatically in scope and critical.
- WinDivert driver issues belong upstream (https://reqrypt.org/windivert.html)
  unless BlurLink stages or configures it unsafely.

## Accepted trade-offs

These are deliberate, recorded decisions rather than oversights. Please
report a *bypass* of one, not the design itself.

- **Host mode introductions are unauthenticated.** Any machine that can
  reach a host's overlay address on UDP 47811 can claim to be a player.
  Host mode **auto-accepts** these introductions, so such a machine can be
  sent copies of Blur discovery replies *addressed to it*. There are no
  accounts, passwords, or cryptography — the identity is "on my overlay
  network, and announced itself".
  **Bounds on the exposure:** the host is only sent copies of discovery
  replies that its own Blur addressed to that exact (LAN address, source
  port) pair — never general traffic, never another player's replies, and
  the original is always reinjected so the host's own LAN is unaffected;
  the accepted-existence of players is visible in the Host tab with
  per-player **Revoke**; the helper still opens no network listener and
  holds no persisted state between runs.
  **The player's LAN address is not a new disclosure:** the host already
  sees it as the source address of the forwarded discovery packet. What is
  newly volunteered is the player's *overlay* address, and only to the host
  the player chose to send to.
- **Host mode's filter is host-side and narrow on purpose**, but changing
  the player roster reopens the WinDivert handle, leaving a brief
  (debounced, logged) interception gap. See `docs/architecture.md`.

No bug bounty is offered at this stage.
