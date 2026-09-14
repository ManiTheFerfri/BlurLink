# Host-side rule ("Host mode") — design

Status: **approved in design review (chat), 2026-09-11.** Not implemented.
Supersedes the `Possible v2 → Host-side helper rule` placeholder in `TODO.md`,
which was gated on "only if research proves it needed".

## 1. Problem

Joining works like this: the joiner's Blur broadcasts a LAN discovery packet;
BlurLink's helper clones it and changes **only** the IPv4 destination to the
host's overlay address (`packet.cpp:CloneWithNewDestination`, bytes 16–19 plus
checksum recompute). Source IP, both UDP ports and the payload stay
byte-identical, and the clone is injected with `Impostor = 1`
(`bridge.cpp:368–391`).

The consequence is the problem this design exists to solve:

- The forwarded packet carries the joiner's **physical LAN address** as its
  source — an address that belongs to no interface on the overlay path.
- The host's Blur, if it receives the forward at all, will normally reply to
  that source address.
- On a genuinely remote overlay (two different physical LANs — the case
  BlurLink exists for), the host cannot route a reply to it. The reply dies at
  the host, the joiner's lobby stays empty, and nothing on the joiner's side
  can detect *why*.

BlurLink v1 has no answer for this, and `docs/troubleshooting.md` currently
misattributes the symptom: its "Does anything answer?" branch sends the user to
check the host's firewall or lobby, which is often the wrong answer for this
failure.

## 2. Goal

Make the host's discovery reply reach each joiner over the overlay, without
touching Blur's protocol, without rewriting payloads, and without disturbing
the host's own LAN behavior.

### Non-goals

- No payload rewrite of any kind (unchanged project rule).
- No Blur protocol constants; nothing is inferred about Blur's format.
- No service, no persistence, no background component.
- No accounts, server, matchmaking, NAT traversal, lobby, or VPN.
- No IPv6, no IP fragments (unchanged).
- No change to the join path. It works today and stays as it is.
- The host's machine will not host *and* join in one session.

## 3. Decisions taken in review

| # | Decision | Rationale |
|---|---|---|
| D1 | Host runs the **full BlurLink app**; host mode is a new UI mode | One codebase; reuses validation, safety limits, logging, elevation flow |
| D2 | Host mode is a **third session type** beside `BRIDGE` and `SNIFF`, not a separate program and not a symmetric rewrite | All new risk sits in a mode that did not exist; the proven join path is untouched |
| D3 | **Players introduce themselves**; the host never types player addresses | A typed list cannot express *which reply belongs to which player*; introductions give per-player matching and scale to any number of players |
| D4 | **Auto-accept on this network**, players visible with Revoke | Convenience chosen deliberately; risk is bounded by the narrow reply filter and recorded in §10 |
| D5 | **Refuse and report** ambiguous replies (two players colliding on LAN address *and* source port) | Preserves the promise that a player only ever receives their own reply |
| D6 | **Keep the narrow WinDivert filter**; accept the brief interception gap when the player set changes | The elevated helper must never be handed more traffic than it needs; recovery is a list refresh |
| D7 | **Refuse broadcast replies** in v1; detect and report them instead | By address alone a broadcast *reply* is indistinguishable from the host's own discovery *request* broadcast; forwarding the request would hand a player an unroutable source and could break the very lobby we are fixing. Gated on verified capture, matching the "no protocol constants without evidence" rule |
| D8 | Attempt a **single-machine loopback end-to-end test** as a manual, elevated script | Catches integration bugs without a second player; falls back to the live session if the driver refuses |

## 4. Prerequisite (must be verified, not assumed)

**Host mode cannot help if the host never receives the forward.** The forward
also carries the foreign source address, so the host's Windows or Blur may
quietly ignore it. This has never been proven: every live test so far had the
peer offline (`TODO.md` → "Peers currently OFFLINE").

Therefore host mode **counts inbound forwards per player and shows the count as
a first-class part of its status**. Zero means the problem is upstream of host
mode entirely, and the app says so instead of implying a firewall problem.

If the live run shows forwards are *not* received, host mode is not the fix and
the join-side alternative in §12 becomes the likelier answer. This is the
primary risk to the whole design and is the first thing the live session
settles.

## 5. Architecture

```
                    host machine                            joiner machine
  ┌──────────────────────────────────────────┐   ┌──────────────────────────┐
  │ BlurLink.Desktop (Host tab)              │   │ BlurLink.Desktop (Join)  │
  │   │ named pipe                          │   │   │ named pipe           │
  │   ▼                                      │   │   ▼                      │
  │ blurlink-net.exe  (HOST mode, elevated)  │   │ blurlink-net.exe (BRIDGE)│
  │   one WinDivert handle, combined filter  │   │   one handle             │
  │   ├─ observe inbound introductions ──────┼◄──┼── introduction packet    │
  │   ├─ observe inbound forwards (count) ───┼◄──┼── cloned discovery       │
  │   └─ capture outbound replies, inject ───┼───┼─► cloned reply           │
  │      a clone to the player's overlay IP  │   │   (delivered normally)   │
  └──────────────────────────────────────────┘   └──────────────────────────┘
```

Two invariants are deliberately preserved:

- **One WinDivert handle** per helper process — host mode uses a single handle
  with a combined filter, not one handle per direction.
- **No network listener.** The helper never binds a UDP port. Introductions are
  *observed* through WinDivert, then reinjected unchanged, exactly like the
  bridge already handles non-matching originals. `docs/architecture.md`'s
  "it has no network listener" claim stays true.

## 6. Components

### 6.1 Helper: `HostSession` (C++)

A new session type owning one WinDivert handle. Responsibilities:

1. Open with the combined filter (§7.4).
2. Observe inbound introductions; maintain the player roster (§6.2).
3. Count inbound forwards per player (the prerequisite signal).
4. Capture outbound replies; match to a player; inject one clone (§8).
5. Detect and report broadcast replies without forwarding them (D7).
6. Rebuild the filter when the roster changes, debounced (D6).
7. Expose counters and roster via the existing `get_status` reply.

Reuses unchanged: the WinDivert API seam, the token-bucket rate limiter, the
dedup cache, the metadata-only logger, counters, and the dashboards.

### 6.2 Player roster

Per accepted player:

| Field | Source |
|---|---|
| overlay address | introduction packet's L3 source (`view.src_ip`) |
| LAN address | introduction packet contents |
| Blur source port | introduction packet contents |
| first seen / last seen | helper clock |
| forwarded count | this helper |

Rules: cap **8** players; match key is **(LAN address, Blur source port)**; a
match key shared by two live players is refused and reported (D5); `Revoke`
removes a player and rebuilds the filter.

A player who stops announcing for **45s** is removed **from the filter** and
marked inactive in the roster. The entry stays visible until revoked or host
mode stops, deliberately: the forwards-heard counter is the §4 diagnostic, and
losing it exactly when a player goes quiet would hide the very signal that
explains why nothing is being forwarded.

### 6.3 GUI: Host tab

Fields: overlay adapter (reuses the existing picker), discovery port (with the
existing bounded detect action), Start / Stop, auto-accept toggle, player list
(overlay address, LAN address, last seen, forwarded count, **Revoke**), and a
status block showing forwards heard, replies forwarded, and any refused
broadcast replies. Prerequisite messaging from §4 appears here.

### 6.4 Core (C#)

`HostFilterBuilder` mirroring `SniffFilterBuilder`/the bridge filter builder:
validates addresses and ports, emits the canonical combined filter string, and
is unit-testable without the driver. Introduction packet encode/decode lives
here too so both the GUI tests and interop can exercise it.

## 7. Wire format and filter

### 7.1 Introduction packet

Fixed-length, 20 bytes, UDP, addressed to the host's overlay IP on BlurLink's
own port. Not JSON, so a value in the contents cannot impersonate a field
(consistent with the existing shadow-proof key-lookup decision).

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `B L N K` |
| 4 | 1 | version (1) |
| 5 | 1 | flags (reserved, 0) |
| 6 | 4 | joiner overlay address |
| 10 | 4 | joiner LAN address |
| 14 | 2 | joiner Blur source port (BE) |
| 16 | 4 | reserved, zero |

Validation: exact length, magic, version, port non-zero. Anything failing is
counted and dropped — never acted on. The packet never contains game, lobby or
payload data, and never will.

### 7.2 Sending

The joiner's helper sends it when the bridge starts, then repeats on a bounded
interval (a few copies at start, then occasionally) so a late-starting host
still learns. Bounded in count and rate **separately** from the clone token
bucket (§8.6), so announcing often can never spend the reply budget.

The helper forges the L3 source to the joiner's own overlay address, so the
packet arrives with a *legitimate* overlay source — unlike the forward. The host
trusts the packet's source for the overlay address and reads the LAN address
from the contents.

The LAN address is not a new disclosure: the host already sees it as the source
of the forwarded discovery packet. The introduction only gives it a name.

### 7.3 Port

BlurLink's own constant (`HostAnnounceUdpPort`), deliberately **not** the
discovery port: sending it to the discovery port would feed garbage to the
host's Blur. This is BlurLink's protocol constant, not an inferred Blur one, so
it does not violate the no-hardcoded-constants rule.

### 7.4 Combined filter

```
(inbound  && ip && udp && udp.DstPort == <announcePort>)
|| (inbound  && ip && udp && udp.DstPort == <discoveryPort>
    && (ip.SrcAddr == <playerLAN1> || ip.SrcAddr == <playerLAN2> || ...))
|| (outbound && ip && udp && udp.SrcPort == <discoveryPort>
    && (ip.DstAddr == <playerLAN1> || ip.DstAddr == <playerLAN2> || ...))
```

Every half is scoped to accepted players, so the helper is never handed more
traffic than the feature needs (D6):

- the first term matches only BlurLink's own introduction port;
- the second matches inbound discovery **from an accepted player's address** —
  this is what makes §4 diagnosable, and the per-player source term keeps the
  host's own LAN discovery traffic out of the filter entirely;
- the third matches outbound UDP leaving the host from the discovery port and
  addressed to an accepted player's LAN address.

Nothing else can match, by construction.

Note the default assumption `udp.SrcPort == <discoveryPort>` (a reply from a
socket bound to the discovery port). This is an inference about Blur and is
**the second thing the live session must confirm**. If forwards are heard but
replies are never captured, the escape hatch is a bounded SNIFF-mode "check
reply shape" action in host mode that observes outbound UDP toward accepted
player addresses and reports the source ports and sizes it actually sees
(metadata only) — detect, never guess.

Every per-player term must change when a player joins or leaves, which means
reopening the handle (D6). Reopens are debounced so a burst of joins causes one
reopen, the event is logged, and a reply that slips past is recoverable by
refreshing Blur's list.

## 8. Reply forwarding

For each captured outbound reply:

1. Match **(destination address, destination port)** against the roster's
   **(LAN address, Blur source port)** — ports are preserved through the
   exchange, so the reply's destination port is the player's Blur source port.
2. No match → reinject, count, done.
3. Match shared by two live players → **refuse**, reinject, count and report (D5).
4. Match → clone with destination changed to that player's overlay address,
   recompute checksums, inject with `Impostor = 1`.
5. Always reinject the **original** unchanged (the project's existing
   preserve-original rule), so the host's own LAN behavior is identical to
   running without BlurLink.
6. Counters: `captured`, `forwarded`, `reinjected`, `dropped`, `injectionErrors`,
   plus per-player `forwarded`. The token bucket applies to injected clones
   only; the bounded repetition of introductions (§7.2) is limited separately,
   so a player announcing often cannot spend the reply budget.

Payload and ports are byte-identical in the clone; only the destination address
changes — the exact mirror of the join-side rule.

### Broadcast replies

If an outbound reply is addressed to a broadcast address, host mode **does not
forward it**. It counts it and reports it in status and in the dashboard, and
the GUI explains that the shape is unverified. Reverse detection is not
attempted (§12).

## 9. Failure handling

| Condition | Behavior |
|---|---|
| No discovery port configured | Host mode refuses to start (same as Research-mode bridge) |
| No overlay adapter selected | Refuses to start, same message style as the bridge |
| UAC cancelled | Does not start (by design) |
| WinDivert driver missing / filter rejected / access denied | Existing error strings reused verbatim |
| Helper lost (3 failed polls) | Existing GUI message; watchdog exits the helper |
| Watchdog fires | Helper self-exits; the elevated process never lingers |
| Second host mode | Refused by the session guard (mirrors the bridge-session guard) |
| Local bridge/sniff running | Starting host mode stops it (one session at a time) |
| Forwards heard = 0 | Explicit "the host is not receiving forwards" message (§4) |
| Replies seen = 0, forwards > 0 | Points at the src-port assumption in §7.4 and offers the shape check |
| Broadcast reply observed | Counted and reported, not forwarded |
| Colliding match key | Refused and reported, naming both players |
| Player stops announcing | Expires after 45s; filter rebuilt |

## 10. Security and privacy

- **Accepted risk (D4).** Introductions are unauthenticated. With auto-accept
  on, anyone else on the same overlay network can be sent copies of Blur
  discovery replies addressed to *them*. Bounded by: the filter (only Blur
  replies aimed at that address, never general traffic), the visible roster,
  and per-player Revoke. This must be recorded as an explicit trade-off in
  `SECURITY.md`, not left implicit.
- **No payloads, ever.** Introductions carry addresses and ports only. The
  logger is unchanged and still has no payload parameter.
- **No new secrets.** No accounts, keys, passphrases or crypto are introduced.
- **No new listener.** The helper still binds no network port (§5).
- **Logging** continues to record metadata only; introductions are logged as
  addresses and ports, consistent with existing `[fwd]` lines.

## 11. Testing

**Managed unit tests (no elevation):** filter building for host mode including
the canonical-form and bounds rules; introduction encode/decode and every
rejection path (length, magic, version, zero port); roster pairing, expiry,
cap, revoke; the collision refusal; per-player matching; rate limiting; and the
property *each player receives only their own reply*.

**Native tests (C++):** host-mode filter string building; introduction parsing
under the existing fuzz harness; roster expiry and cap; debounce/reopen
behaviour. `CloneWithNewDestination` is unchanged and already covered.

**Interop script:** host mode start and stop; second host mode refused; session
guard honoured; the narrow filter line reported for the log; a second host mode
while a bridge runs (and vice versa); no leftover helper process. These run
unelevated and therefore stop at the driver boundary — they prove mode
lifecycle and error paths, not a real packet arriving. Classification outcomes
that need packets (a broadcast reply being refused, a collision being reported)
are covered by the unit and native tests against the classifier directly,
since the interop script cannot inject packets without elevation.

**Loopback end-to-end (manual, elevated, D8):** host mode plus a bridge on one
machine over the loopback adapter with a stand-in for Blur, exercising
introduction → roster → reply → clone → receipt. Attempted; falls back to the
live session if WinDivert will not cooperate with loopback broadcast. Not in CI.

**Discipline:** each new guard test is verified to fail against the unguarded
code before being accepted, as in the previous review passes.

## 12. Alternatives considered

**Reply-only host rule.** Narrower and simpler, but it does not deliver the
two-way result requested and would need revisiting; the reply half is where the
matching complexity lives anyway, so the saving is small.

**One symmetric mode.** Conceptually cleaner and removes the host/join
asymmetry, but rewrites the proven join path, changes saved settings, and
blurs the split that makes the UI and limits explainable. Rejected (D2).

**Separate host program.** Duplicates the pipe, elevation, watchdog, logging and
every safety limit — two copies to keep in sync. Rejected.

**Join-side source-address rewrite.** Have the join-side clone set the source to
the joiner's own overlay address, so the host's reply becomes natively routable
and no host-side rule is needed at all. Cheapest option and it fixes the
*forward* as well as the reply. Not chosen here, but it remains the most likely
answer if §4 shows forwards are not received, and it is worth revisiting after
the live run either way.

**Reverse detection to separate broadcast replies from request broadcasts.**
Deferred. Requires a verified payload or behavioural signature; explicitly out
of scope until evidence exists.

## 13. Documentation changes

- `docs/architecture.md`: add host mode; state that the one-handle and
  no-listener invariants still hold; document the reply-forwarding rule.
- `docs/troubleshooting.md`: **fix the existing misleading branch** under
  "Does anything answer?" — "Nothing arrives" currently implies host firewall,
  closed lobby, or wrong host IP, and omits a reply that never left the host.
  Add that branch.
- `SECURITY.md`: the D4 trade-off.
- `README.md` / `TODO.md`: host mode, and move this item out of the gated v2
  list.

## 14. Open questions for the live session

1. **Does the host receive the forward at all?** (§4 — the prerequisite.)
2. Does the host's Blur reply **from the discovery port**, as §7.4 assumes?
3. Is the reply **unicast** to the player's LAN address, or broadcast? (Decides
   whether the D7 path ever needs building.)
4. Is the reply's **destination port** the player's Blur source port, i.e. are
   ports preserved through the exchange as the clone assumes?
5. Does the host's Blur **answer a forward whose source is a foreign LAN
   address** at all, regardless of routing?

Answers to 2–4 need metadata only; no payload inspection is required for any of
them.
