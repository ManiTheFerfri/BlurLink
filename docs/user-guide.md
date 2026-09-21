# BlurLink user guide

**BlurLink does not create a VPN.** You need a working virtual LAN first
(your LAN emulator/VPN), where you can already ping each other by overlay
IP (e.g. host `100.96.47.177`, joiner `100.96.21.89`).

## Hosting (for your friend)

1. Start your VPN/LAN emulator and note your overlay IP (e.g. `ipconfig`).
2. Open Blur, create a LAN lobby, and share the overlay IP with the joiner.
3. On first lobby creation, allow Blur through Windows Firewall when asked
   (otherwise the joiner's discovery can't reach the game).
4. If your friends are on a *different* physical network from you, also run
   **Host mode** — see below. Without it BlurLink's work is all on the joiner,
   and that is not enough across a genuinely remote overlay.

## Host mode (for the host)

Run this on the machine **whose Blur lobby the others join**, when the
players are on different physical networks.

1. Blur is running with its LAN lobby open. Go to the **Host** tab, select
   your overlay adapter (the one your friends reach you on), press
   **Start Host Mode**, and approve the UAC prompt.
2. **Look at "forwards heard" first.** Each friend who starts their bridge
   sends you a small introduction, and their discovery forward should show up
   as a heard packet. **If it stays 0, their traffic is not reaching you at
   all** — Host mode cannot help from there and the problem is on the join
   side. See `docs/troubleshooting.md` step 3.
3. Players appear in the list as they announce themselves. Nothing needs to be
   typed for them. Acceptance is automatic so nobody has to wait on you, but
   every entry has a **Revoke** button.
4. Each player's own replies are copied to *their* overlay address. Your own
   LAN is untouched — the original reply is always left in place, so players
   sitting next to you keep working exactly as before.

Things worth knowing:

- **Host or join, not both.** Starting Host mode stops a bridge or a sniff,
  and starting one of those stops Host mode. One session at a time.
- A silent player is dropped from the filter after about 45 seconds. If one
  player's Blur stops listing the lobby, that is usually why — have them
  refresh Blur's LAN list.
- Broadcast replies (`x.x.x.255`) are **refused and counted**, not forwarded.
  That is deliberate: until a real capture verifies the packet shape,
  guessing there could break the very lobby you are fixing. If those refusals
  climb, that is useful evidence — record it for a future profile.

## Join

1. Open BlurLink → **Join**.
2. Enter the host's overlay IPv4 (strict IPv4, e.g. `100.96.47.177`).
3. Select *your* overlay adapter.
4. The discovery port is fixed at 50001 (verified by capture + live
   lobby) — it is already filled in. Only if you cleared it, restore
   50001; Advanced manual entry can still override it, but the default
   flow never needs it. (Broadcast destination and optional signature
   can still be entered manually.)
5. Keep **Preserve physical LAN broadcast** enabled unless you know why not.
6. Press **Start Bridge** → approve the UAC prompt (helper only). Starting
   the bridge also starts watching for the host's answer automatically —
   one action, no second button.
7. Check the active filter line + route warning. No warning + correct
   filter = good to go.
8. Launch Blur normally, open its LAN games list. The `forwarded` counter
   should tick as Blur broadcasts; the card shows who answers.
9. Play. When done, **Stop Bridge** (interception ends immediately; the app
   confirms the helper actually exited).

## Routing & firewall diagnostics

- **Route test** (Settings): verifies Windows routes the host overlay IP
  through your selected adapter. A mismatch warning means the overlay isn't
  set up right — BlurLink will not fix routes for you in v1.
- **Ping** is ICMP-only and only runs when you click it.
- If Windows Firewall prompts for Blur, allow it on the overlay network
  profile. Never disable the firewall.
- **Copy diagnostics** gives you adapter + route metadata (no packet
  contents) to share when asking for help.

## Limitations (v1)

- Discovery port fixed at 50001 (verified); Research mode survives only
  as Advanced manual entry for overrides.
- One broadcast destination per run; IPv4 only.
- If the lobby lists but joining fails, the host reply may embed a
  physical LAN IP — documented in troubleshooting; no auto-fix in v1.

## Logs, watchdog, uninstall

- App log (metadata only): `%LocalAppData%\BlurLink\logs\blurlink.log`.
  Logging level is set in Settings. Logs never contain packet payloads.
- Helper log (metadata only): `%LocalAppData%\BlurLink\logs\helper.log`,
  mirrored from the helper's console output (rotates at 1 MB to
  `helper.log.1`). View its tail in Settings → Helper log viewer.
- Helper log (metadata only): `%LocalAppData%\BlurLink\logs\helper.log`.
- The helper's black console window is a live dashboard, not a dead
  prompt: it prints its startup, every command it receives, every forwarded
  packet (`[fwd] src -> broadcast => host`), sniff observations (`[heard]`
  + final port table), and shutdown with final counters. If it ever sits
  empty while the app claims to work, that itself is the symptom — copy
  what you see (or don't) into a bug report.
- **Helper log viewer (Settings):** shows the tail of helper.log inside the
  GUI (last ~200 lines, newest last) with Refresh now, Copy log, and an
  Auto-refresh toggle. This is the same dashboard the console prints — so
  you can read what the elevated bridge actually did without switching
  windows or after it has exited.
- Blur status: the app automatically attaches to a Blur.exe you started
  yourself ("attached") or one it launched ("launched"/"watched"), and the
  bridge can stop when Blur exits. No manual attach step exists.
- If the GUI crashes, the helper notices the silence and exits by itself
  within ~15s (watchdog), closing WinDivert. If the bridge launched Blur,
  it also stops automatically when Blur exits (disable on the Join tab if
  your Blur uses a launcher stub that exits immediately).
- Uninstall: delete the app folder. The WinDivert driver unloads when the
  last handle closes; `sc stop WinDivert` + `sc delete WinDivert` (admin)
  or a reboot clears residue. No service or firewall rules are installed.
