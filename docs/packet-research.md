# Blur packet research (safe procedure)

> Capture **only** on systems and networks you own or are explicitly
> authorized to test.

## Easiest path: automatic detection (built in)

Join → Advanced → **Detect discovery port**. The helper listens for
15 seconds (SNIFF mode: nothing is blocked, modified, or reinjected —
packets flow untouched) and counts outbound broadcast UDP packets per
destination port. While it listens, open Blur and refresh its LAN list a
few times. The app then suggests the port — press **Use port N** and it
fills the discovery port field.

- Bounded: 15s, 200 packets max, broadcast destinations only.
- Metadata only (ports + counts). No payloads, ever.
- If it hears nothing, you refreshed too late/early — just run it again
  while refreshing.

Second question the app answers: **Listen for replies** (same place, needs
the bridge running). It watches inbound UDP to the discovery port for 15s
and tells you exactly which IP:port answered — or that nothing came back.

## CORRECTION (2026-09-12, later the same day): "hosting is silent" is NOT established

*(See "Timing, measured with `--timestamps`" at the end of this section for the
measurement that replaced the guesswork.)*

**Read this before trusting the section below.** A 240-second watch of outbound
traffic on the discovery port, run while the game was believed to be on its
lobby screen, caught **eight packets**:

```
out 10.0.0.10:50001 -> 255.255.255.255:50001 len=52 id=32748
    payload=0f0000000000002c01000000 753eae27f6b82f50 01000000
out 10.0.0.10:50001 -> 255.255.255.255:50001 len=52 id=32749
    payload=0f0000000000002c01000000 6a90dc53334ea787 01000000
out 10.0.0.10:50001 -> 255.255.255.255:50001 len=52 id=32750
    payload=0f0000000000002c01000000 ab0070bf639e81b4 01000000
out 10.0.0.10:50001 -> 255.255.255.255:50001 len=52 id=32751
    payload=0f0000000000002c01000000 997a8c25692f3795 01000000
(each of the four was captured twice, byte-identical, same IP ID)
```

### Timing, measured with `--timestamps` (2026-09-12, 45s window)

The interval matters more than the count, and this is the first run that could
measure it:

```
+4.430s  out 10.0.0.10:50001 -> 255.255.255.255:50001 id=32754  (seen twice)
+13.352s out 10.0.0.10:50001 -> 255.255.255.255:50001 id=32755  (seen twice)
+45.013s capture ended -- nothing in between
```

So the sender is **intermittent, in short bursts**, not on a steady timer:

- two queries **8.9 s apart**,
- then **31.7 s of complete silence** inside the same window,
- and in the earlier 240 s window, only 4 distinct queries total, and *not*
  evenly spread: they cluster in the first minute and then go quiet (estimated
  positions below), so "about one per minute" is an average that hides the shape.

That is exactly the regime a short watch cannot judge: **31.7 s of silence is
normal behaviour**, so a 20 s window can land entirely inside a gap and see
nothing while the game is broadcasting perfectly well. The "hosting is silent"
claim below was a single 20 s sample, and this measurement explains how it could
have been produced by a game that was not silent at all.

One more thing this run shows, and it is a correction to the *architecture*
premise rather than to this document: the game was on its **lobby** screen and
still broadcasting queries. So "the host is silent, only the joiner asks" is not
true in general — a Blur sitting on its lobby screen keeps asking the LAN for
games (which makes sense: that screen lists them). Hosting and broadcasting are
not mutually exclusive states, and any troubleshooting text that implies they
are needs fixing. The user's own account of this session, below, is what makes
this an *observation* rather than an inference: nothing on the host was being
touched while these packets were captured.

Three things follow, and the first one is a correction of this document:

1. **The "silent while hosting" finding below cannot stand as written.** It was a
   **20-second** watch. A sender broadcasting roughly once a minute would be
   missed by a 20s window most of the time, so that measurement could not   distinguish "never broadcasts" from "broadcasts more slowly than the window".
   The observed rate here is 4 queries in 240 s — about one per minute on
   average, but clustered early and separated by gaps longer than a minute (see
   the estimates below) — which is exactly the regime a 20 s window is blind to.
   **The claim is withdrawn**; what was needed was a timestamped window long
   enough to catch a slow period, which is why `--timestamps` was added to the
   capture tool. That window now exists (the 45 s one above) and the user
   confirms nothing was clicked in either, so the withdrawal stands on direct
   evidence rather than on suspicion.
2. **The 8 bytes at offset 0x0C are a PER-REQUEST NONCE, not a constant.** Four
   queries seconds apart carried four different values. This refines the earlier
   reading: the host's reply *echoes* those 8 bytes, which is why query and reply
   shared them — the reply echoes whatever nonce the request carried. A replay
   harness should therefore vary this field rather than assume any particular
   value is meaningful.
3. **Every query is captured twice with identical bytes *and* identical IP ID**
   (32748 twice, 32749 twice, …). Consecutive IDs across queries say these are
   four datagrams, each observed twice — consistent with a limited broadcast
   surfacing more than once at the network layer, not with the game sending pairs
   (two separate sends would normally take different IDs). Benign for the
   bridge: identical identity is what the dedup backstop collapses, so the
   duplicate forwards once and is suppressed once.

So the honest state of the "is the host quiet?" question is: **a searching Blur
provably broadcasts repeatedly, and a Blur sitting on a LAN lobby screen queries
the LAN on its own — the 20 s sample was the mistake, and the claim does not come
back.** What is still unknown is only the *shape* of that self-querying (see the
estimates below), not whether it happens.

### The host's own account of the session (added 2026-09-12, after the fact)

**Two LAN lobbies were created once — at the moment the capture script was
created — and the host machine was then left untouched for the rest of the
session.** No lobby was re-created, no LAN list was refreshed by hand, nothing
was clicked, and there was no long gap that anything could be attributed to.
This corrects an earlier reading in this document (repeated in `TODO.md` as "at
a lobby the user had just re-created"), and it matters in three ways:

1. **Every query in both windows was the game's own.** No user input triggered
   the two queries in the 45 s timestamped window, nor the four in the 240 s
   window before it. "A Blur on its LAN lobby screen asks the LAN by itself" is
   therefore *observed* — which is what keeps the withdrawn "hosting is silent"
   claim withdrawn.
2. **Lobby creation was already inside a capture; that window just has no
   timestamps.** The 240 s run (`out/lobby-create.log`, 17:37:20 + 242 s)
   predates `--timestamps`, so creation cannot be aligned to the queries exactly.
   Where they *fell* can still be estimated, by interpolating the catch-all
   capture against the two steady telemetry streams inside it (`5.57.39.8:1552`,
   408 packets across the window, and `195.18.10.202:9993`, 62): the four queries
   land at roughly **+10–14 s, +16–20 s, +31–48 s and +161–183 s**. Three in
   about the first minute, one about two-thirds through, then ~60 s of quiet.
   These are estimates, not measurements — the two clocks disagree by ~15%, so
   read them as ±10–20 s — and a repeat with `--timestamps` would settle it
   exactly.
3. **So the creation question is narrowed, not closed — and it needs no more
   user action.** An early cluster is *consistent with* creation-triggered
   traffic, but the same shape (short burst, then a long gap) appears in the 45 s
   window **12 minutes later, where nothing was created at all**. That makes "the
   lobby screen queries intermittently by itself" the better-supported reading,
   and it does not matter to the product either way: the bridge forwards whatever
   the game sends, whenever it sends it.

## Finding: a HOSTING Blur is silent (2026-09-12, measured) — SEE THE CORRECTION ABOVE

A live one-machine capture while Blur **hosted a LAN game** (Blur.exe bound to
`0.0.0.0:50001` discovery and `0.0.0.0:3074` gameplay) produced:

```
capture: ALL outbound udp, 20s       -> 71 packets, 0 broadcast, 0 to/from port 50001
capture: outbound udp dst :50001 20s -> 0 packets
capture: outbound udp dst :3074  10s -> 0 packets
capture: inbound  udp dst :50001 10s -> 0 packets
```

The 71 packets were DNS and launcher/service telemetry, nothing to do with the
LAN discovery. So **the host does not broadcast anything**: it binds 50001 and
waits. The discovery *request* comes from the side that is searching, which is
consistent with the 2026-09-09 joiner-side evidence (4/4 broadcasts to
`255.255.255.255:50001`). **That conclusion is retracted — see the note directly
below.**

> **RETRACTED 2026-09-12 — this section's conclusion does not hold.** *"The host
> does not broadcast anything"* came from a 20 s window that fell inside a gap:
> both a timestamped 45 s window and a 240 s one caught the host's Blur querying
> the LAN on its own, with the host machine untouched. The section is kept as the
> record of the mistake; the correction at the top of this document supersedes
> it, and only the *test-plan* consequences below still stand.

Two consequences worth keeping:

- **You cannot test the host side without a real query.** Host mode's premise is
  "the host's Blur answers a player's query"; with no query there is nothing to
  answer, so a host-only capture proves nothing either way. Capture the query
  from a *searching* Blur first, then replay it at the host.
- **`forwards heard` reading zero while hosting is expected**, not a fault —
  the host has nothing to forward until a player's query arrives.

## Finding: the real discovery request, byte for byte (2026-09-12, measured)

Captured live from a **searching** Blur (the joiner side) on `Example-Overlay-LAN`
(`10.0.0.10`), Blur.exe PID 12345, while the Find Game screen was open.
This is the first genuine game payload observed rather than a stand-in:

```
out 10.0.0.10:50001 -> 255.255.255.255:50001  len=52  id=32739
  payload (24 bytes)
  0f0000000000002c010000008a656eb70adfd31801000000
```

What this settles and what it changes:

- **The destination is the LIMITED broadcast `255.255.255.255`**, not the
  subnet-directed `10.0.0.255`. A capture filtered on the /24 broadcast
  matches nothing — which is exactly what happened in a first attempt, whose
  stage spent its whole 45s window finding zero packets.
- **Source port `50001` == destination port `50001`.** The searcher broadcasts
  *from* the discovery port. This is the design's assumption (a) — "the host's
  Blur answers *from* the discovery port" — now confirmed against real bytes,
  not only against the single 2026-09-09 capture. Both ports are configurable
  (`blurPort` / `discoveryUdpPort`) and both default to 50001.
- **The payload is NOT `BLSIMFWD`.** That string is the *test injector's*
  stand-in, and its help text wrongly claimed it was "what Blur's discovery
  sends". It is not. The real payload is the 24 bytes above. Nothing shipped
  depends on the stand-in (the shipped `payloadPrefixHex` default is **empty**,
  and `IsPayloadPrefixMatch` treats an empty prefix as "match anything"), so
  real traffic is not refused — but the injector's payload was never a faithful
  shape, and any future prefix rule must be derived from these bytes.
- **The same 24 bytes carry no ASCII text at all**, so there is nothing here to
  read as a server name. `0f` (15) leads; a `01 00 00 00` word appears twice.
  One 4-byte field, `0adfd318`, parses as the IPv4 address `10.223.211.24` —
  **not** this machine's own LAN address (`10.0.0.10`), so in the *request*
  the field is not the sender's address. What it is remains unknown, and the
  embedded-address question that matters is about the host's **reply**, which
  we have still never seen. Do not build on this field.

The capture reported the packet **twice**, byte-identical including the IP ID
(`32739`). Two genuinely separate datagrams from Blur would normally carry
different IP IDs, so the likelier reading is one datagram observed twice (a
limited broadcast can surface more than once at the network layer). Either way
it is benign for the bridge: identical identity is precisely what the dedup
cache collapses, so the duplicate would forward once and be suppressed once.
A capture of 4+ packets would settle which explanation holds.

## Finding: the real host ANSWER, byte for byte (2026-09-12, measured)

**This is the single most important capture in the project so far.** It is the
first time any part of host mode's premise has been observed in real game code.
Replaying the captured query as an inbound packet (source `10.0.0.200:50001`,
destination this machine `10.0.0.10:50001`) made the local hosting Blur
answer:

```
out 10.0.0.10:50001 -> 10.0.0.200:50001  len=188  id=38506
  payload (160 bytes)
  0f0000000000002c020000008a656eb70adfd31800000000c41e678600000000
  cc430441f35614e300f85c020500000048006f00730074000000000000000000
  0000000000000000000000000000000000000e00193555770000000000000000
  cfb116d1397e9b89c993e643cf6784dba390b6708e7ef493c0a8000a020c0a00
  000a020c00ff00ff000000ff00ff000001140177000000000b000201a837ef2d
```

### What the envelope proves (the design's two assumptions)

Host mode's whole reply path rests on two assumptions that until now came from
one 2026-09-09 capture. Both are now confirmed against real game code:

- **The host answers FROM the discovery port.** Source is `50001`, not an
  ephemeral port. Assumption (a): **confirmed.**
- **The host answers TO the port it was queried from, on the querying address,
  by unicast.** Destination is `10.0.0.200:50001` — exactly the injected
  source. Assumption (b): **confirmed.** It does *not* broadcast its answer.

The second point is why host mode exists, and the reason is now visible rather
than inferred: Blur sends its answer to the address it saw, which for a player
across a tunnel is an address the host cannot route to. Re-addressing a copy of
that unicast answer is the entire feature.

### What the payload contains

The reply is the query's own format, extended. Byte offsets into the 160:

| offset | bytes | reading |
|---|---|---|
| 0x00 | `0f000000` | same leading word as the query |
| 0x04 | `0000002c` | same as the query |
| 0x08 | `02000000` | **the query had `01000000` — message type 1 → 2** |
| 0x0C | `8a656eb7 0adfd318` | **echoed verbatim from the query** |
| 0x30 | `48006f00730074 00` | **UTF-16LE host name: "Host"** |
| 0x78 | `c0a8000a` `020c` | **`192.168.0.10:3074` — this machine's Ethernet address** |
| 0x7E | `0a00000a` `020c` | **`10.0.0.10:3074` — this machine's overlay address** |

The fields at 0x0C are a **request correlation token**: the host echoes the 8
bytes it received, so the searcher can match an answer to its question. The two
endpoints at 0x78/0x7E are the decisive part — see below.

### The embedded-address question: measured, and the answer is YES

`docs/architecture.md` and `TODO.md` both carried an open question: does the
host's answer merely *address* the envelope to the player, or does it also
**embed the host's own LAN address inside the message text**? If it embeds it,
re-addressing the envelope is not enough and the player would try to connect to
an address on the host's LAN that they cannot reach.

**It embeds it.** Two 4-byte values in the reply are exactly this machine's own
addresses, each immediately followed by the 2-byte little-endian value `020c`
= **3074**, Blur's gameplay port. That is an endpoint list: the host telling the
searcher where to connect.

This does **not** automatically mean payload rewriting is required, and it
should not be read that way. Note *which* two addresses it lists:

- `192.168.0.10:3074` — the physical LAN address, unreachable from the friend.
- `10.0.0.10:3074` — **the overlay address**, i.e. the address the friend's
  machine can reach across the tunnel, on the virtual LAN they already share.

So the reply already advertises an address the friend can reach. Whether Blur
**uses** that entry — or tries the physical one first and gives up — is the
question that decides whether the "no payload rewrite, ever" rule ever has to
change. **Only the two-machine session can answer it**, and it is now the single
highest-value thing left to measure. Do not pre-emptively build a rewriter.

### Delivery is proven, so these results are about Blur, not the harness

A replay is only meaningful if the packet actually reaches the game. That link
had never been tested, so it was proved in isolation first: a UDP socket was
bound on a spare port, the injector sent one inbound packet at it, and the
socket **received all 24 bytes**. So an injected inbound packet genuinely
reaches a listening socket, and the silence below is Blur's behaviour rather
than a packet that never arrived.

### The one result that is NOT yet explained (be careful here)

A later run, two minutes after the success above, produced **zero replies** to
four injections. The run varied two things deliberately:

| t | request token | arrival interface | result |
|---|---|---|---|
| 3 s | fresh (synthetic) | overlay | no reply |
| 8 s | fresh, different | overlay | no reply |
| 14 s | repeat of t=3 | overlay | no reply |
| 20 s | repeat of t=8 | Ethernet | no reply |

An earlier run had answered the *overlay* arrival and ignored the *Ethernet*
one, which suggested the arrival interface mattered. This run answers the
overlay arrival with nothing, so **that explanation is dead.**

Two further runs narrow it, and neither explanation above survives:

- **Exact reproduction failed.** The identical query, token and player that was
  answered at 17:15 was replayed at 17:22 and produced nothing.
- **A new lobby did not help.** Three injections 12s apart, each from a
  *different* player address, with Blur confirmed running and still bound to
  `0.0.0.0:50001` and `0.0.0.0:3074` — produced nothing. (Corrected 2026-09-12 on
  the user's own account: the two LAN lobbies were created **once** and the host
  was then left untouched for the rest of the session — no lobby re-created
  mid-run, no LAN list refreshed by hand, nothing clicked. So the silence was not
  a reaction to host-side activity, which removes "something changed on the host"
  from the candidate explanations.)
- **Our side was controlled in the same minute.** The delivery control was
  re-run immediately afterwards and still passed: an injected inbound packet
  reached a bound socket, all 24 bytes. So the silence is the game's behaviour,
  not a fault in the injector, the driver, or the filter.

Every injection ever made, and every answer ever seen:

| when | token | player | arrival | reply |
|---|---|---|---|---|
| 17:15 | real | `10.0.0.200` | overlay | **answered** |
| 17:15 | real | `192.168.0.200` | Ethernet | none |
| 17:17 | synthetic ×2 | three players | both | none |
| 17:22 | real | `10.0.0.200`, `.201` | overlay | none |
| 17:25 | real | `.210`, `.211`, `.212` | overlay | none |

**Exactly one answer, to the first injection, and never again.** The unifying
readings left are either a one-shot/cooldown behaviour in the game, or a lobby
state we cannot observe from the host side. A short window right after entering
the host screen fits the 17:15 timing. What the user's account *does* exclude is
blaming the host's own use of Blur: nothing was re-created or refreshed while
those injections were being replayed, and the game was still querying the LAN by
itself in the same period (the 17:37 and 17:49 windows above).

**Therefore: "Blur answers a real query" is proven — the answer's destination
was a fabricated address that appeared nowhere else, so the injection caused
it. "Blur answers every query" is not.** Any harness or test that replays a
captured query must confirm the reply **in the same run** and report
inconclusive rather than fail when the game stays silent. This is a two-machine
question now: a real searching Blur queries continuously, so the real
interaction does not depend on this variance.

## Manual path: Wireshark

## Goal

Fill `profiles/research-mode.json` with verified values:

```json
{
  "profileName": "Blur LAN (verified YYYY-MM-DD)",
  "discoveryUdpPort": 12345,
  "broadcastDestination": "255.255.255.255",
  "payloadPrefixHex": "42 4C ...",
  "notes": "capture file, Blur build, adapter"
}
```

Do not invent these values. One real capture beats ten guesses.

## Procedure

1. Host PC: start a Blur LAN lobby. Joiner PC: open Wireshark on the wired
   LAN adapter, capture filter `udp` (capture filters are fine — this is
   your own LAN; keep the capture host-only).
2. On the joiner, click Blur's LAN browser / refresh server list.
3. Stop the capture. Find the discovery request:
   - Destination is usually `255.255.255.255` or the subnet directed
     broadcast (`ip.dst == 255.255.255.255 || ip.dst == 192.168.x.255`).
   - Note **UDP destination port** (that is `discoveryUdpPort`).
   - Note **UDP source port**: fixed or ephemeral? Retried how often?
4. Find the host response: filter `udp.srcport == <discoveryPort>` or the
   host's IP. Note direction, ports, and timing.
5. Critical check — **does the response embed an IP?** Look at the response
   payload: does it contain the host's *physical* LAN address (e.g.
   192.168.x.y)? If yes, the lobby may list but joining can fail over the
   overlay, because Blur will try to connect to an unroutable address.
   Record this; do NOT hand-edit payloads — v1 cannot rewrite them.
6. Payload signature: if the first N payload bytes are stable across
   refreshes/restarts (compare 3+ captures), record them as
   `payloadPrefixHex` (space-separated hex, e.g. `42 4C 55 52`). If they
   vary (session cookies, counters), leave the signature **empty** — an
   unstable signature would silently block discovery.

## Evidence needed before any protocol profile

- [ ] ≥3 captures showing the same dst port + broadcast dst
- [ ] Source-port behavior documented (fixed/ephemeral)
- [ ] Response direction + ports documented
- [ ] Embedded-IP verdict (present/absent, offset unknown in v1)
- [ ] Signature stability verdict (stable → record; unstable → empty)

## Entering verified values

Join tab → discovery port, broadcast destination, optional signature →
Start Bridge → confirm `forwarded` counter increments when refreshing
Blur's LAN list. Then follow the manual checklist in
`docs/troubleshooting.md` items 1–10.
