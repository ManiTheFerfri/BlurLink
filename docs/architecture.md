# BlurLink architecture

Status: **research/MVP (v1)**. No Blur protocol constants are hardcoded;
the default profile is `profiles/research-mode.json` (port unknown).

## No-backend design

There is no server, VPS, database, account, lobby, matchmaking, NAT
traversal, VPN, tunnel, or game server anywhere in v1. The only network
traffic BlurLink creates is a unicast copy of the user's own Blur discovery
broadcast, addressed to a host overlay IP the user already reaches through
their own VPN/LAN emulator. Gameplay traffic never touches BlurLink.

## Roles

- **BlurLink.Desktop** (C# WPF, MVVM, runs **non-elevated**): all UI,
  adapter enumeration, route pre-check, config persistence, launching
  `Blur.exe`, and launching the helper elevated via UAC (`runas`).
  It never touches packets.
- **blurlink-net.exe** (C++20, runs **elevated** only while the bridge is
  started): owns the single WinDivert handle, classifies, clones, injects,
  counts. It listens on one local named pipe; it has no network listener.
- **BlurLink.Core** (C#): validation, filter building, transform reference,
  rate limiter, adapters/routing, config store, diagnostics text.
- **BlurLink.Contracts** (C#): config schema + IPC message shapes shared by
  GUI and (by parity) the helper.

## Precise packet flow (Join mode)

1. User enters host overlay IP + discovery port + broadcast dst (+ optional
   payload prefix), selects the overlay adapter, presses **Start Bridge**.
2. GUI validates everything, builds the narrow filter
   `outbound && ip && udp && udp.DstPort == P && ip.DstAddr == B`,
   runs a `GetBestRoute` lookup, warns on adapter mismatch, then starts
   `blurlink-net.exe --pipe <random> --token <256-bit>` elevated.
3. Helper validates the `start` command again (never trusts the GUI),
   opens WinDivert (`NETWORK` layer, priority 0) with that exact filter.
4. Blur.exe sends a LAN discovery broadcast → WinDivert diverts it.
5. Helper checks the optional payload prefix in code (the WinDivert filter
   stays port+address only, keeping it provably narrow), checks the dedup
   cache, checks the token-bucket rate limiter (10/s, burst 20).
6. Helper clones the datagram: **only** IPv4 dst := host overlay IP;
   payload and UDP ports byte-identical; checksums recomputed with
   `WinDivertHelperCalcChecksums`; injected outbound with `Impostor=1`.
7. Helper reinjects the **original** byte-for-byte (default; disable in
   Advanced only if you understand the LAN impact).
8. The host's unmodified Blur replies; the joiner's unmodified Blur receives
   it. BlurLink synthesizes nothing *into* the game on either side. The
   address the reply *is sent to* is where the two sides differ — see
   **Host mode** below.
9. **Stop Bridge** (or app exit, or Blur exit detection where wired) closes
   the WinDivert handle: interception ends instantly.

## Host mode (the host machine's side)

Join mode works because the *host's* unmodified Blur replies — but it replies
to the joiner's **physical LAN address** (that is the source address it saw on
the forwarded packet), which on a genuinely remote overlay the host cannot
route back to. Host mode fixes that half: it sends a **copy** of each reply to
the player's overlay address and reinjects the original untouched.

**Measured against the real game (2026-09-12).** Replaying a captured real
discovery query at a hosting Blur produced its real answer, and the answer did
exactly what this section assumes: **unicast to the querying source address,
from the discovery port to the querying port** (`50001 -> 50001`). It never
broadcast. So the premise here is no longer inferred from one capture — it was
reproduced from real game code. Raw bytes and byte offsets are in
`docs/packet-research.md`; the one part two machines still must settle is
whether the forward crosses the tunnel at all.

Host mode is the **third session type** beside bridge and sniff. Like them it
is exclusive — starting one stops the others, and the **one WinDivert handle**
invariant (and with it "no persistence, no service") is unchanged. The helper
still opens **no network listener**: player introductions are *observed* through
the driver, never received on a socket.

### The introduction packet

Nothing on the network tells the host a player's overlay address, so the join
side says so itself. When a bridge has seen its first forward it sends a small
fixed **20-byte** packet to the host's overlay address on UDP port **47811**: a
magic prefix, a version, the player's overlay IPv4, the player's LAN IPv4, and
the player's Blur source port. It carries addresses and a port only — never
game or lobby contents, and it is parsed strictly (length, magic, version,
addresses, port) so it can never throw on garbage.

The pairing it produces is the whole trick: overlay address ← the packet's own
source (legitimate, so it survives the overlay either way), LAN address ← the
packet contents. That pair is what lets the host match a reply to the right
player.

### The host filter

Three OR-ed terms, each scoped to the accepted players' LAN addresses:

```
(inbound  && ip && udp && udp.DstPort == 47811)                          # introductions
(inbound  && ip && udp && udp.DstPort == <discovery> && ip.SrcAddr == <player…>)  # forwards
(outbound && ip && udp && udp.SrcPort == <discovery> && ip.DstAddr == <player…>)  # replies
```

The **player's Blur source port is deliberately not a filter term.** If the
host's reply ever arrives on an unexpected destination port, the code-side
matcher must be able to see it and report it; a port term here would swallow
that silently. Matching happens in code on the (LAN address, Blur source port)
pair, so **two players behind the same LAN address** (two homes on the same
common router subnet) are still told apart, and two live players that collide
on the pair are **refused** rather than guessed at.

### The reply rule

Per forwarded packet: look up the destination, clone with **only the IPv4
destination** set to that player's overlay address, recompute checksums, send
with `Impostor=1`. Ports and payload stay byte-identical, exactly like the join
side. The original is **always** reinjected byte-for-byte, so the host's own LAN
behaviour is identical to running without BlurLink. A refusal never silently
drops: the original still flows.

Broadcast replies (`x.x.x.255`) are **refused and counted**, not guessed at: by
address alone the host cannot tell a broadcast *reply* from its own Blur
broadcasting a discovery *request*, and forwarding that request would hand a
player an unroutable source address. The path stays closed until a real capture
verifies its shape — the same no-unverified-constants rule as everywhere else.

### Roster, expiry and the filter rebuild

Introductions build a roster capped at **8 players**; a player is dropped from
the filter after **45s** of silence (a crashed Blur cannot say goodbye). Expiry
removes the entry from the filter but keeps its counters visible, so the
`forwards heard` diagnostic that explains a silence is never erased by it.

WinDivert fixes its filter when the handle opens, so a roster change means
**reopening the handle** — and during that instant a reply in flight can slip
past. That is structural, not a bug. The rebuild is **debounced (~2s)** so a
burst of joins costs one reopen rather than one each, and it is logged with its
interception gap named, so it is never a mystery. The alternatives were worse:
one broad filter would hand the *elevated* helper far more traffic than the
feature needs.

### The prerequisite, made visible

Host mode counts the forwards arriving from players and shows them. **Zero**
means the forward is not reaching the host at all — in which case Host mode
*cannot* help, and a join-side source-address rewrite is the likelier fix. The
Host tab says that plainly instead of implying a firewall problem.

## Research sniffer (discovery-port detection)

Join → Advanced → Detect. A second WinDivert session in `SNIFF` mode with
filter `outbound && ip && udp && (broadcast dsts)` counts destination ports
for ≤15s / ≤200 packets. SNIFF means observe-only: the driver never blocks,
modifies, or reinjects, so there is nothing to loop or leak. Results are
per-port counts (metadata). Bridge and sniffer are mutually exclusive —
starting one stops the other; `stop` halts either.

## Constraints & known limitations

- IPv4 only; IPv6 and IP fragments are never cloned (fragments are
  reinjected and counted).
- One broadcast destination per bridge run (global, directed, or multicast).
- No automatic payload rewrite: if the host reply embeds a physical LAN IP
  and joining fails, see `docs/troubleshooting.md` — a verified packet
  format is required before any v2 profile may rewrite payloads.
  **It does embed one (confirmed 2026-09-12)** — but it embeds *two* endpoints,
  and the second is the host's **overlay** address, which a player across the
  tunnel can already reach. Since the message contains a usable endpoint
  already, a rewrite may never be needed; whether the game *uses* that entry is
  what the two-machine session decides. Do not build a rewriter before it.
- Same-subnet physical LAN behavior is preserved by default (original
  reinjected).
- Reinjection-loop strategy: (a) filter matches broadcast only so unicast
  clones can never re-match; (b) per WinDivert docs, a handle does not
  re-capture its own reinjections (only *other lower-priority* handles can);
  (c) clones are injected with `Impostor=1` (TTL-decrement backstop);
  (d) bounded `DedupCache` skips re-cloning identical echoes while still
  reinjecting them.

  The echo cache keys on `(src ip/port, dst ip/port, IP ID, UDP length)`, and
  the IP ID is the field that does the work: an IPv4 ID of **0 is not a value
  but "stack, assign me one"**, and Windows gives each locally injected DF
  datagram a fresh consecutive ID (RFC 6864: atomic datagrams). So a genuine
  Blur retransmit always carries a *new* ID and is forwarded as designed, while
  a **re-captured reinjection carries the ID it was reinjected with** and is
  suppressed. `dedupSkipped` therefore stays 0 in normal use and moves only on
  a real loop — which is what it is for. Verified at the driver level on
  2026-09-12 with a pinned ID (`scripts/test-e2e.ps1`,
  `bridge-echo-backstop-suppresses-repeat`).
- Helper runs only while the bridge is started; no service, no persistence.
- **Watchdog:** the helper exits by itself (default 15s, `--watchdog-sec`
  2..300) when authenticated GUI traffic stops — e.g. the GUI crashed.
  A 120s grace applies before the first command so slow UAC approval never
  triggers it. `watchdogSec` is reported in `get_status`.
- **Blur-exit auto-stop:** the GUI watches a Blur.exe it launched and stops
  the bridge when Blur exits (opt-out on the Join tab).
- **OS check:** the helper gates on manifest-proof `RtlGetVersion`
  (`VerifyVersionInfo` lies without a manifest); an `asInvoker` manifest
  with Win10/11 `supportedOS` GUIDs is embedded for MSVC builds.
- **IPC hardening:** GUI→helper calls are serialized (no pipe interleaving),
  UTF-8 without BOM, connect retries while the helper is alive (slow UAC),
  short timeouts on stop/poll paths so Stop never hangs on a dead helper.
- **Logging:** `%LocalAppData%\BlurLink\logs\blurlink.log` (level from
  Settings, rolling 1MB×3). The logger API has no payload parameter, so
  packet contents cannot be logged by construction. The helper mirrors its
  console dashboard to `logs\helper.log` (`--log-file`, 1MB rotation, same
  no-payload API), and the GUI reads its tail for Settings → Helper log
  viewer (bounded end-seek read, no writer locks, auto-refresh off-tab).
