# BlurLink — work log

## Project decisions (do not regress)

- **Slim-only shipping (2026-09-09):** the deliverable is
  `dist/BlurLink/BlurLink.exe`, framework-dependent (~3.5 MB, needs .NET 8
  Desktop Runtime). `build-portable.ps1` builds slim by default; `-Full`
  (self-contained, ~140 MB) and `-Sfx` are legacy fallbacks — do not build
  them unless explicitly asked.

## Host mode — implemented and round-trip PROVEN on one machine (2026-09-12)

Design: `docs/superpowers/specs/2026-09-11-host-side-rule-design.md`
Plan:   `docs/superpowers/plans/2026-09-11-host-mode.md` — all 15 tasks executed
(its boxes are reconciled and it carries a STATUS table).

Round-trip status: **proven at the driver level on a single machine** — host
mode and the Join-mode bridge each ran against a real WinDivert handle with
synthesised packets, 42 of 42 checks green (`scripts/test-e2e.ps1`). What is
*not* proven is a real Blur lobby over two machines; see "Needs live Blur
traffic" at the end of this file for exactly which claims remain open.

Why: the join-side clone changes only the destination, leaving the joiner's
physical LAN address as the source. On a genuinely remote overlay the host
cannot route a reply back to it, so the reply dies at the host. Host mode
forwards each of the host's Blur replies to the matching player's overlay
address. Decisions taken in review: full app with a new Host tab; host mode as
a third session type (bridge/sniff/host, one at a time); players introduce
themselves rather than the host typing addresses; auto-accept (recorded as a
SECURITY.md trade-off); keep the narrow filter and accept the brief
interception gap on a roster change; refuse and report broadcast replies until
a capture verifies their shape.

- [x] Task 1 — constants (`HostAnnounceUdpPort` 47811, cap 8, expiry 45s) and
      config fields (`hostAutoAccept`, `hostAcceptedPlayers`), incl. the export
      schema allow-list
- [x] Task 2 — `AnnouncePacket` codec: fixed 20-byte layout, strict decode,
      never throws on garbage
- [x] Task 3 — `HostFilterBuilder`: three terms, each scoped to accepted
      players; the player port is deliberately absent so a port mismatch stays
      reportable
- [x] Task 4 — native `EncodeAnnounce`/`DecodeAnnounce` and
      `BuildHostFilterString`; the player port is not even a parameter
- [x] Task 5 — `HostRoster`: cap 8, expiry keeps the entry but drops it from the
      filter, collision refused only against *live* players
- [x] Task 6 — `ClassifyHostPacket`: forward / reinject / refuse-broadcast /
      refuse-ambiguous, every outcome carrying a reason string
- [x] Tasks 7–9 — `HostEngine` (portable): `ValidateHostConfig`, dispatch of
      introductions / forwards / replies, counters, expiry, debounced filter
      rebuild; plus `HostSession` (Windows) as the thin WinDivert I/O around it,
      mirroring `Bridge`'s start/stop/worker and its driver error strings.
      **Deviation from the plan, agreed mid-implementation:** rather than testing
      `HostSession` (which needs the Windows SDK), all the logic moved into the
      portable `HostEngine` so it *can* be tested; `HostSession` is verified by
      compiling the helper (`-Werror`), by the interop checks (Task 13), and —
      since 2026-09-12 — by the elevated harness, which drives it against a real
      handle on a single machine (the loopback route turned out not to exist;
      Task 14 amendment below). What remains is a real Blur lobby over two
      machines, not this plumbing. Counters that depend
      on a send actually succeeding are reported
      back by the caller (`NoteCloneSent`/`NoteCloneFailed`), because a decision
      is not a delivery.
- [x] Task 10 — helper IPC: `start_host` / `revoke_host_player` and the
      host-mode status block (`hostActive`, `hostForwardsHeard`,
      `hostRepliesForwarded`, `hostBroadcastReplies`, `hostAmbiguousReplies`,
      `hostUnmatchedReplies`, `hostReinjected`, `hostInjectionErrors`,
      `hostAnnounceRejected`, `hostFilterReopens`, `hostCollisions`,
      `hostLastNote`, `hostPlayers[]`), plus the managed contract shapes
- [x] Task 11 — the join side announces itself: the bridge sends the 20-byte
      introduction once it has seen its first forward, to `hostOverlayIp:47811`,
      only when `localOverlayIp` is set (no overlay address = no introduction,
      because the host could not reply)
- [x] Task 12 — the Host tab: `HostViewModel` on the `JoinViewModel` seam
      (`ApplyStatus`/counters), `HostView.xaml` (adapter picker, start/stop,
      forwards-heard prerequisite line, player list with Revoke), wired into
      `MainWindow` and `MainViewModel`, plus `HostView` in `ViewSmokeTests` so
      XAML parse errors and bad bindings fail a test
- [x] Task 13 — interop coverage: 14 new checks (status fields, port/adapter
      refusals, the announce-port refusal, reaching the driver, session release
      after a failed host start, host-then-bridge, second-host refusal, revoke
      paths, stop halts host mode, dashboard line) → 45 checks total. **Also
      fixed a real bug:** the script's no-argument fallback compiled a stale
      hardcoded source list that predated host mode, so it silently reused an
      old `out/interop/blurlink-net.exe`; the list now mirrors
      `src/BlurLink.Net/CMakeLists.txt`
- [x] Task 14 — **AMENDED, see below** (the loopback script as specified is
      impossible; the finding is recorded instead)
- [x] Task 15 — docs: troubleshooting (the host-side reply branch that used to
      be misdiagnosed as a firewall problem), architecture (a Host mode
      section), `SECURITY.md` + `docs/security.md` (the unauthenticated
      introduction trade-off), `README.md` (both halves, counters, security
      notes), `docs/user-guide.md` (Host mode walkthrough) and this file

### HIGH SEVERITY, found by the harness on its first real run: the wrong WinDivert shutdown constant wedged Stop()

**The bug.** `windivert_api.h` recorded the WinDivert shutdown enum as
`RECV=0, SEND=1, BOTH=2` — an ordinal guess. The real header
(`basil00/Divert/include/windivert.h`, WinDivert 2.2) defines them as **bit
flags**: `WINDIVERT_SHUTDOWN_RECV = 0x1`, `SEND = 0x2`, `BOTH = 0x3`.

So every `Stop()` called `WinDivertShutdown(h, 0)`, which fails with
`ERROR_INVALID_PARAMETER`. The return value was ignored, so the blocked
`WinDivertRecv` was **never released** and the next line — `worker_.join()` —
blocked forever.

**Blast radius, all from one wrong number:**
- `stop` never replied: the command loop was wedged, so the GUI's Stop would
  hang (host mode *and* the bridge — `Bridge::Stop` had the identical shape).
- The **watchdog could not rescue it**: its exit path calls `host.Stop()` too,
  so it fired, logged "stopping the bridge now", and then hung on the same call.
  That breaks the documented promise that the helper exits by watchdog (15s
  default) if the GUI disconnects — in practice only Task Manager ended it.
- Fixing it needed a **kill of an elevated process**, since the helper also held
the WinDivert handle, the bridge-session lock and its own log file.

**Why nothing caught it before.** Every automated test runs unelevated, where
`WinDivertOpen` fails — so `Stop()` was only ever exercised on a session that
never opened a handle, where it returns immediately. The bridge had the same
untested path. This is exactly the gap the injector harness was built to close,
and it took one real run to find it.

**Why it hid so well.** Filter rebuilds kept working the whole time, because
`RebuildFilterIfDue` calls `close_(old)` right after the (failing) shutdown, and
closing the handle unblocks a pending recv by invalidating it. The recovery
path masked the broken one.

**Fixes:**
1. Corrected values, and they now live in a new platform-independent
   `src/BlurLink.Net/include/blurlink/windivert_abi.h` — *because* getting one
   wrong does not fail to compile, it fails inside the driver at runtime. It
   carries `static_assert`s pinning every value, states which groups are flags
   and which are ordinals, and records this incident so nobody "simplifies"
   them back to 0,1,2. `windivert_api.h` now includes it instead of
   re-declaring its own copies.
2. A native regression test (`TestWinDivertAbi`, +9 checks) asserts the values,
   so the portable suite — which runs on every build — now covers them.
3. Structural hardening in both `HostSession::Stop()` and `Bridge::Stop()`: a
   bounded wait on a `worker_exited_` flag *before* joining, and if the worker
   has not exited, `WinDivertClose` to unblock its recv. A blocked recv must
   not be able to hang `Stop()`, because hanging there also disables the
   watchdog. (Deliberately not `std::thread::native_handle()` + `WaitForSingleObject`:
   that is a Win32 HANDLE under MSVC but opaque under MinGW, and the interop
   harness builds with MinGW — it failed to compile there first.)

**Verification status of the fix: VERIFIED at the driver level (2026-09-12).**
The constant, the assertions and the hardening build clean under `-Werror`
(MSVC/CMake *and* the MinGW path the interop script uses); the portable suite is
green at 92,857 checks; interop is green at 45 checks; and the elevated
end-to-end harness passed **all 24 checks, 0 failed, 0 skipped**, including
`stop-clears-host-mode`, `helper-exits-on-shutdown` and `no-helper-left-behind`.
(That run was host mode alone. The harness later grew to cover the Join-mode
bridge as well, and now runs **42 checks** — see "Both sessions in one elevated
run" below.)

The helper's own dashboard is the proof, and it is worth keeping because the
contrast with the broken run is the whole story:

```
BEFORE  [14:58:28] ---- STOP REQUESTED ----
        (nothing for 120s, then the watchdog fired and hung on the same call)

AFTER   [15:10:04] ---- STOP REQUESTED ----
        [15:10:04] host mode stopped: captured=6 forwardsHeard=1 forwarded=1
                   reinjected=6 refusedBroadcast=1 ambiguous=0 unmatched=1 errors=0
        [15:10:04] ---- SHUTDOWN REQUESTED (GUI closed) ----
        [15:10:04] helper exiting cleanly (bridge stopped, driver closed).
```

Same second, and the counters match exactly what the harness asserted
independently over the pipe. No stray processes were left behind.

### The elevated run's results (2026-09-12)

**All 24 checks passed** on the final run (the run before it passed 15 of 15
*functional* checks and failed only the stop, which is the bug above). This is
the first real exercise of host mode's inbound path:

```
PASS start-host-ok / host-filter-is-host-modes-own / host-filter-has-no-players-yet
PASS player-appears-in-roster / roster-keeps-announced-lan
PASS roster-keeps-announced-blur-port / roster-entry-is-in-filter
PASS filter-rebuilt-for-the-player / filter-now-scopes-the-player
PASS prerequisite-forward-heard
PASS reply-forwarded-to-the-player / original-reply-still-reinjected
PASS clone-logged-with-the-overlay-destination
PASS wrong-port-reply-reported / wrong-port-reply-not-forwarded
PASS dot255-player-joins-the-filter
PASS broadcast-reply-refused / broadcast-reply-not-forwarded
PASS refused-reply-still-reinjected / offfilter-broadcast-never-forwarded
PASS stop-clears-host-mode / helper-exits-on-shutdown / no-helper-left-behind
```

The helper dashboard recorded the whole thing, including the exact filters:
```
filter : (inbound && ... udp.DstPort == 47811) || (inbound && ... udp.DstPort == 50001 &&
         (ip.SrcAddr == 10.20.30.255 || ip.SrcAddr == 10.20.30.40)) || (outbound && ...
         udp.SrcPort == 50001 && (ip.DstAddr == 10.20.30.255 || ip.DstAddr == 10.20.30.40))
[host-fwd] reply 10.5.206.140:50001 -> 10.20.30.40:51234 => player 25.1.2.3
```

**Two of the failures in the first run were my test's bugs, not the product's,
and each taught something worth keeping:**

1. **`forwards heard` cannot move for a player that has not been accepted.**
   With an empty roster the filter is the introduction term *alone* (by design —
   the narrow-filter rule), so a forward from an unknown source cannot match it.
   The stage was asserting the prerequisite *before* the introduction, which can
   never pass. Fixed by ordering it after acceptance. Worth knowing for real
   use: `forwards heard == 0` is ambiguous, since it also reads 0 for a friend
   who has not announced yet, not only for one whose traffic is not arriving.
   The Host tab's wording already hedges this correctly.
2. **The broadcast refusal is only reachable through a `.255` LAN address.**
   The filter scopes outbound replies to the accepted players' LAN addresses,
   so a reply to `255.255.255.255` is excluded *by the filter* and never reaches
   the classifier — the counter could never move. It becomes reachable exactly
   in the case `docs/architecture.md` already documents as the limitation: a
   player whose own LAN address ends in `.255`. The stage now uses such a player
   on purpose, which proves the refusal end to end **and** proves the documented
   caveat is real rather than theoretical. A separate check records that the
   true limited-broadcast address is excluded by the filter instead.

### SECOND HIGH SEVERITY, found the moment the bridge ran under the driver: four reinjects taken while holding the session lock

**The bug.** `Bridge::WorkerLoop` has a `send_original` lambda that reinjects the
captured packet byte-for-byte, and it takes `mutex_` to count the reinject. Four
"we decided not to forward this" branches called it *from inside a scope that
already held `mutex_`*:

| branch | when it fires in real use |
|---|---|
| payload-prefix gate | the filter is port+address only by design, so any other app (or a changed Blur payload) sending to the discovery port lands here |
| echo backstop | a re-captured reinjection — the case the cache exists for |
| rate limiter | Blur broadcasting faster than the configured rate (default 10/s) |
| clone-failure handler | a malformed packet `CloneWithNewDestination` rejects |

`std::mutex` is not recursive, so a second acquire on one thread is undefined
behaviour that in practice deadlocks. The first elevated bridge run **hung** on
the payload-prefix path — and because the command loop needs the same lock, the
helper could not answer `stop` either, so the watchdog could not rescue it
either. Same failure shape as the shutdown-constant bug, different cause.

**Why nothing caught it.** Identical reason to the shutdown bug: every
automated test runs unelevated, so `WinDivertOpen` fails and `WorkerLoop` never
starts. No amount of logic testing reaches a lock-ordering bug in a loop that
only exists once a handle is open.

**Why host mode was never affected.** `HostSession` sends *outside* `mutex_` and
delegates every decision to `HostEngine`, which has its own lock — deliberately
structured that way when the engine was extracted. Only the bridge had the shape.

**Fix:** all four sites close their lock scope before reinjecting, and the
lambda now carries the invariant in a comment — it *must not* be called with
`mutex_` held — with the incident written down next to it, since `-Werror` and
1,000 unit tests are both blind to this one.

### The echo backstop does not mean what "the same packet" sounds like

The harness's first attempt at the echo stage sent the same bytes twice and the
bridge forwarded both — and it was **right to**. `--verbose` on the helper
printed the identity key it actually saw:

```
dedup: 10.5.206.140:51234 -> 255.255.255.255:50001 id=45133 udpLen=16 => new (clone)
dedup: 10.5.206.140:51234 -> 255.255.255.255:50001 id=45134 udpLen=16 => new (clone)
```

An IPv4 ID of **0 is not a value — it is "stack, assign me one"**, and Windows
assigns a fresh consecutive one to each locally injected DF datagram (RFC 6864
calls such packets atomic). So two `id=0` injections are never identical on the
wire, and the identity check — which keys on the ID, exactly as documented —
was correct to see two distinct packets. A non-zero ID **is** delivered
verbatim: the same run's `--vary-id` packets arrived as `id=1..5`.

That is the design working, not failing: "genuine retransmits carry fresh IP
IDs, so they still forward" is the documented intent, and the backstop's real
target — a re-captured *reinjection*, which carries the ID it was reinjected
with — is what it suppresses. The **stage** was wrong, so the injector gained
`--ip-id` to pin one. With the identity pinned the counter moves exactly once
(`dedup-skipped=1`).

### Both sessions in one elevated run: what the harness covers now

The harness drives **both** WinDivert sessions. It was renamed
`scripts/test-host-e2e.ps1` → **`scripts/test-e2e.ps1`** once the old name
became a lie; `-Phase host|bridge|all` selects one. The bridge half needed a new
injector kind, `blur-broadcast`: an **outbound** packet from our own LAN address
to the broadcast address on the discovery port — exactly the shape Blur's own
LAN announcement takes, and what the bridge's filter matches.

Bridge stages, each asserting a different promise:

| stage | what it proves |
|---|---|
| `bridge-reports-its-own-filter` | the bridge opens a real handle and reports *its* filter (`outbound && ip && udp && udp.DstPort == … && ip.DstAddr == 255.255.255.255`) |
| `bridge-captures-blur-broadcast` | a Blur-shaped broadcast is captured, not ignored |
| `bridge-forwards-to-host-overlay` | it is cloned to the host's overlay address |
| `bridge-original-still-reinjected` | the original still leaves unmodified (preserve-original) |
| `bridge-announces-itself-to-the-host` | **the join side introduces itself** — Task 11's feature, previously untested at runtime |
| `bridge-payload-prefix-gate-holds` | a same-shaped packet from another app is preserved, never forwarded |
| `bridge-echo-backstop-suppresses-repeat` | the dedup backstop suppresses a same-identity repeat |
| `bridge-rate-limit-refuses` | a burst is refused rather than hot-forwarded |
| `second-helper-refused-while-bridge-live` | the cross-process lock, non-elevated logic aside, holds against a real live bridge |
| `session-released-after-bridge-stop` | `stop` releases it, verified from the *other* process (the same process short-circuits the check) |
| `bridge-stop-clears-mode` / `helper-exits-on-shutdown` | stop is clean; no helper left behind |

**Final result: 42 of 42 checks pass, 0 failed, 0 skipped, exit 0** (host 24,
bridge 18). The helper's own dashboard agrees, and the bridge counters are worth
keeping because every one of them matches a predicted value:

```
host mode stopped: captured=6 forwardsHeard=1 forwarded=1 reinjected=6
                   refusedBroadcast=1 ambiguous=0 unmatched=1 errors=0
bridge stopped:    captured=10 forwarded=3 reinjected=10 dropped=5 errors=0 dedup-skipped=1
probe helper:      WARN start refused: another BlurLink bridge is already active on this PC
                   (then) info bridge started   # after the first bridge stopped
helper exiting cleanly (bridge stopped, driver closed).
```

10 injected, 10 captured; 1 + 1 + 1 forwarded (initial, the first of the
same-identity pair, one inside the rate allowance); 1 suppressed as an echo;
5 refused by the limiter; all 10 reinjected. No orphans.

Verification state after this pass (all re-run green on the final tree):

| suite | result |
|---|---|
| managed | **220 / 220** |
| native portable | **92,870 checks / 0 failures** (+13: `TestEchoBackstopIdentity`) |
| helper build | clean under `-Werror` (MSVC/CMake *and* the MinGW path) |
| `tools/hostsim` build | clean under `-Werror` |
| interop | **45 / 45** (1 honest SKIP) |
| e2e, elevated | **42 / 42, 0 skipped**, exit 0 |

`TestEchoBackstopIdentity` is the new portable regression test and it pins the
layer that had to be cleared before blaming the driver: it builds the exact
bytes the injector sends, parses them with the real parser, and keys the
identity the way `WorkerLoop` does — proving that *identical bytes do yield an
identical identity and a suppressed echo*. It passes, which is precisely why
the live result pointed at the IP ID rather than at the cache.

### Live session against the real game, 2026-09-12 — what it proved

One machine, real Blur (PID 15832), real WinDivert, real packets. Four results,
in the order they matter:

1. **A searching Blur broadcasts — and so does a Blur parked on its LAN lobby
   screen. (CORRECTED 2026-09-12: "a hosting Blur is silent" is WITHDRAWN; it
   rested on a 20 s sample, and the timestamped 45 s window caught two queries
   with the host machine untouched. See `docs/packet-research.md` → CORRECTION →
   "The host's own account of the session".)** What still stands is the test-plan
   consequence: the host has nothing to *answer* until a query arrives, so a
   host-only capture can prove nothing about the reply path.
2. **The real discovery request was captured byte for byte** (`10.88.14.114:50001
   -> 255.255.255.255:50001`, 24-byte payload). It is **not** the ASCII
   `BLSIMFWD` stand-in the injector used, and the injector's help text wrongly
   claimed it was — corrected. Nothing shipped depended on the stand-in
   (`payloadPrefixHex` defaults to empty and matches anything), but a future
   prefix rule must come from these bytes.
3. **Injected inbound packets DO reach a bound UDP socket.** This had never been
   tested — every earlier result only proved the helper's *capture* saw injected
   packets, which is a different link. Proved in isolation with a socket bound on
   a spare port that received all 24 bytes. This is what makes a one-machine
   replay against a real game a valid question rather than a shot in the dark,
   and it is why the results below are about Blur rather than about the harness.
4. **The real host ANSWER was captured** (160 bytes), which confirms host mode's
   two load-bearing assumptions against real game code and settles the
   embedded-IP question — the reply *does* embed endpoints, and one of them is
   the overlay address. Details, offsets and raw bytes:
   `docs/packet-research.md`.

Honest gaps from this session, and they are the important part of the record:

- **Exactly one answer was ever seen, and no attempt reproduced it.** The exact
  same query, token and player that was answered at 17:15 was replayed at 17:22
  and got nothing; three injections from three different players at 17:25 also
  got nothing, with Blur confirmed running and still bound to `50001` and `3074`.
  **Corrected 2026-09-12 on the user's own account:** the two LAN lobbies were
  created **once** and the host was then left untouched for the rest of the
  session — no lobby re-created mid-run, no LAN list refreshed by hand, nothing
  clicked. (An earlier wording here said "a freshly re-created lobby", which
  overstated what happened.) So the silence was not a reaction to anything done
  on the host, and the same account upgrades the opposite finding: every query
  captured in the 17:37 and 17:49 windows was the game's own, emitted with no
  user input at all.
- **Our side was controlled in the same minute.** The delivery control was
re-run right after and still passed — an injected inbound packet reached a
bound socket. So the silence is the game, not the injector, driver or filter.
This is the difference between an honest negative and a guess.
- **Consequence:** "Blur answers a real query" is proven; "Blur answers every
query" is not. A test that replays a captured query must confirm the reply in
the *same* run and report inconclusive, never fail, when the game stays silent.
A short window right after entering the host screen fits the timing, but no
reading is distinguished by the evidence we hold.
- **Nothing here touched the tunnel.** The forward still has to cross it on two
machines, which is also what makes this variance a non-issue in real use: a
real searching Blur queries continuously.

### The real query is now the harness's fixture (2026-09-12)

`scripts/test-e2e.ps1` no longer injects the ASCII stand-in. It injects the
**real captured 24-byte query** (`$RealQueryHex`), and the configured
`payloadPrefixHex` default moved from `424C53` ("BLS", the stand-in's own
letters) to `0F0000` (the real packet's leading bytes) — **the harness's
`-PayloadPrefixHex` parameter, not the product's default: the shipped
`payloadPrefixHex` is still empty and still matches anything**
(`BlurLinkConfig.cs`, `profiles/research-mode.json`). So the capture path, the
prefix gate, the echo backstop and the rate limiter are all exercised against
the shape the game actually sends — and the gate is proven to **admit** a genuine
Blur packet rather than only a test string.

The prefix-gate *negative* is now `$RealQueryMutatedHex`: the identical packet
with **one byte flipped** (leading `0f` -> `1f`). That is a stronger negative
than the old `ZZZIMFWD`, because it shows the gate refusing something
indistinguishable from a real query except for that byte.

Honest cost, recorded in the script's own notes: the injected broadcasts are no
longer strictly inert. If a real Blur is running and hosting, it may treat an
injected broadcast as a genuine query and answer it — to the test's own Blur port,
where nothing listens, so the reply goes nowhere.

**That cost is now enforced instead of documented.** The harness checks for a
running `Blur.exe` in its preconditions and refuses to start while one is up
(exit **2**, `CANNOT RUN: Blur is running (PID ...)`), because the interference
runs both ways and neither direction is harmless: the game can answer our
injected query, *and* the game's own broadcasts are matched by the same bridge
filter the bridge stages count on — so a live game can flip a stage to PASS or
FAIL for reasons unrelated to BlurLink. `-AllowLiveBlur` overrides it
deliberately, warns which results to distrust, and says to re-run with the game
closed before believing a failure. The guard sits **ahead of the elevation
check** on purpose: it is the safety precondition and it needs no admin rights,
so the real reason is reported on the first try rather than after an elevation
round trip.

**PENDING VERIFICATION:** the change builds and the script parses, and the
injector was confirmed to accept every new argument — but the elevated run that
proves all 42 checks still pass has **not** been done. A fixture that silently
broke a stage would be worse than the stand-in it replaced, so this must be run
before the harness is trusted again.

**The Blur guard, by contrast, is verified for real — both branches.** Blur (PID
15832) happened to be running when the guard was added, which made it testable
without elevation, and that is exactly why it was placed ahead of the elevation
check. Unelevated and unmodified: the script printed
`CANNOT RUN: Blur is running (PID 15832)` and exited **2**; with
`-AllowLiveBlur` it printed the warning and continued to the next precondition
(`this test needs Administrator`), which proves the override overrides and that
nothing else ran. `-AllowLiveBlur` therefore does *not* weaken the "never a false
pass" rule: it only lets a run proceed into a check that can still fail on its
own merits.

### `--timestamps`, and the argument-parsing bug it exposed (2026-09-12)

`hostsim capture` gained `--timestamps`, which prefixes each packet with its
time since the capture started (`+12.345s`) and the summary with the window
length. It exists because a packet list with no timing cannot separate a
**periodic** sender from a **one-shot** one — which is precisely the mistake
recorded in `docs/packet-research.md` ("a hosting Blur is silent", from a window
too short to see a slow period).

Adding it surfaced a real bug in the injector's parser, and the shape is worth
remembering: flags that take no value are listed explicitly, and a flag
**missing** from that list does not error at the flag — the parser silently
swallows the *next* argument as its value, so the failure appears one argument
later as `unexpected argument '120'`. `--timestamps` ate `--seconds` and died on
the bare number; `--vary-id` had failed the same way before, when it happened to
be last. The list is now one named table with that recorded beside it, so the
next flag goes in one place.

This is the second time the injector's own argument handling, not the code under
test, was the defect — both found by running it for real rather than by reading it.

### Live-session tooling added 2026-09-12 (`hostsim capture`)

The injector gained the two things a live session with the real game needs, and
both are verified:

1. **`capture`** — an observe-only SNIFF handle that prints each matching
   packet's payload as hex, so a *real* packet can be replayed verbatim with
   `--payload-hex` instead of a stand-in. This is the only way to ask the real
   game what it does with a real packet.
2. **`--stop-after N`** — stop once N parseable packets have been seen, so a
   live capture needs no manual timing. Verified with a 120s ceiling, and with
   a control to prove the behaviour is conditional rather than accidental:

```
outbound dns, stop-after 1, ceiling 120s -> stopped after 2.2s, 1 packet
any 5 outbound udp,      ceiling 120s    -> stopped after 1.3s, 5 packets
control: no stop-after,  ceiling 8s        -> used the whole 8.0s
```

One measurement worth keeping: `--in --port N` matches the *destination* port,
which for inbound traffic is the **client's** ephemeral port, not the service's.
A first attempt at `--in --port 53` therefore matched nothing for two minutes --
the tool was right and the filter was wrong. The help text now says so.

Also fixed by building it: the injector's source file list is hard-coded in
`scripts/test-e2e.ps1`'s fallback build, and adding `packet.cpp` to the CMake
target broke the script's build until both lists were updated. That is the
same drift that made the interop script reuse a stale helper binary; both lists
now carry a comment saying they are one list.

### Task 14 amendment: a loopback end-to-end script is not possible

The plan's Task 14 called for a single-machine loopback harness (host mode +
a bridge + a stand-in Blur over 127.x). **That cannot work, and it is not an
environment quirk.** The official WinDivert 2.2 documentation states:

> Note that WinDivert considers loopback packets to be outbound only, and will
> not capture loopback packets on the inbound path.

(https://reqrypt.org/windivert-doc.html — also vendored in spirit by
`third-party/WinDivert/README.Windivert`, whose feature list includes loopback
support.)

Host mode's two *inputs* are both `inbound` filter terms: the introduction
(`inbound && udp.DstPort == 47811`) and the player's forward
(`inbound && udp.DstPort == <discovery> && ip.SrcAddr == <player LAN>`).
Loopback traffic can never match an `inbound` term, so a loopback harness would
report `SKIP` forever — structurally, not because a driver was missing. The
plan's fallback ("falls back to the live session and loses nothing but an
afternoon") was written before this was known; it is now the *only* option for
the inbound half.

**DECIDED AND BUILT (same day): option 1 — a synthetic-peer injector.**

`tools/hostsim/hostsim.cpp` (+ `CMakeLists.txt`) and the harness
(originally `scripts/test-host-e2e.ps1`, **renamed to `scripts/test-e2e.ps1`**
once it covered the bridge too) replace the impossible loopback script. The injector fakes a remote player:
`intro` and `forward` are injected **inbound** (`WinDivertSend` with
`Outbound = 0`); `reply` and `broadcast` are injected **outbound** to play the
host's own Blur. The script drives the real helper over its real pipe and
asserts each stage separately.

Mechanism, from the official WinDivert 2.2 docs — every part of this is
load-bearing and was verified, not assumed:

- *"if the pAddr->Outbound field is 0, the packet is injected into the inbound
  path"* **and** *"only the Outbound field, and not the IP addresses in the
  injected packet, determines the packet's direction"* — so a foreign source
  address can be faked, which is the entire point.
- *"Injected packets can be captured and diverted again by other WinDivert
  handles with lower priorities."* The injector therefore opens at priority 1
  while host mode sits at 0.
- *"For packets injected into the inbound path, the pAddr->Network.IfIdx and
  pAddr->Network.SubIfIdx fields are assumed to contain valid interface
  numbers... or from the IP Helper API"* — resolved with `GetBestInterface()`.
- The injector's handle uses the filter `"false"` (a documented literal; the
  filter language defines TRUE as 1 and FALSE as 0). It therefore diverts
  **nothing**, so it cannot disturb host mode's traffic or re-capture its own
  injections — and no `WINDIVERT_FLAG_*` value had to be guessed, because the
  flags are not vendored here.

**Why it is a separate tool and never the helper:** it can synthesize arbitrary
packets from arbitrary addresses. The elevated shipped binary must never be able
to do that, so `blurlink-net.exe` gained no injection path.

**Stages the script asserts** (each reported separately, so a partial result is
still informative): the prerequisite (`forwards heard`), the player appearing in
the roster with its announced LAN address and Blur port, the debounced filter
rebuild scoping the player, a reply being cloned out *and logged with the
player's overlay destination* plus the original still being reinjected, a
broadcast reply being refused and counted, a right-address/wrong-port reply
being reported instead of forwarded, and a clean stop with no helper left.

**Honest verification status of this new harness.** Built clean under
`-Werror`; its argument validation and error paths exercise correctly and the
script's own plumbing is verified for real (pipe handshake, injection failures,
polling, cleanup, exit codes). What is **not** verified is the injection itself:
that needs the driver and an elevated shell, which is unavailable here. So the
round trip is still unproven end to end — the harness is what makes proving it
possible on one machine. Unelevated the script exits **2 (cannot run)**, never a
false pass.

Bugs found by actually running it, both in code written for this test:
- `Connect-Pipe` was missing `$w.AutoFlush = $true`, so every command sat in the
  writer's buffer, the helper never saw `hello`, and `ReadLine()` blocked
  forever. It would have hung on the first command for anyone running it.
- Reading a status field with `$s.hostFilter.Contains(...)` killed the script
  with "cannot call a method on a null-valued expression" against a helper that
  predates the field. Field reads are now null-safe, so a stale binary produces
  a FAIL instead of a crash.

### Also fixed while building the harness: the Host tab's filter was always blank

The Host tab binds `ActiveFilter`, and `HostViewModel.ApplyStatus` filled it
from `status.Filter` — but the helper's `filter` field is the **bridge's**, and
the two sessions are mutually exclusive, so it is empty exactly when host mode
is running. Host mode's filter line therefore never appeared. The helper now
emits `hostFilter` alongside `filter`, the contract carries it, and the view
model reads its own. Guarded by a test that sets both fields to *different*
values, so reading the wrong one fails (verified: reverting the fix produces
exactly that one failure), plus an interop check that is honestly `SKIP`ped
rather than passed when host mode cannot start.

### Observation (not fixed): `adapterIfIndex` is validated but unused
The Host tab (and the Join tab, identically) requires an overlay adapter, but
`adapter_if_index` appears in no filter term — the filter is purely port- and
address-scoped. So choosing a different adapter changes nothing at runtime. This
predates host mode and matches the bridge's behaviour, so it was left alone;
widening the filter with an `ifIdx` term would make it genuinely narrower and is
recorded under "Possible v2" instead.

### Task 7–9 blocker (decided)

Resolved by moving all host *logic* into the portable `HostEngine` and leaving
`HostSession` as pure WinDivert I/O. See the Tasks 7–9 entry above; recorded
here because the plan as written put `HostSession` tests in a target that
cannot compile Windows-only code.

### Task 7–9 blocker (needs a decision)

The plan put the `HostSession` tests in `tests/BlurLink.Net.Tests`, but that
target deliberately compiles **platform-independent sources only** (config,
packet, json_min, announce, host_roster, host_classify) and links no Windows SDK
or WinDivert. `host_session.cpp` needs both, so those tests cannot live there.
The decision core they would have covered is already tested exhaustively by
Tasks 5–6; what is left is WinDivert plumbing observable only with a driver.

### Known limitation found during implementation

`ClassifyHostPacket` refuses any destination ending in `.255` as a broadcast
reply, which is correct on a /24. On a subnet wider than /24 (e.g. /23), a player
can legitimately hold a `.255` LAN address, and their replies would be refused.
The refusal is reported (reason `broadcast-reply`), never silent. Refinement
would be to let an exact (address, port) match against a live player win over the
`.255` heuristic — deliberately not done, because the approved spec says refuse
broadcast first and changing approved design mid-implementation is not mine to do.

### The five live-session open questions (spec §14) — FOUR NOW MEASURED

A one-machine session against the real game on 2026-09-12 answered four of
these five by replaying a captured real query at a hosting Blur. Evidence and
raw bytes: `docs/packet-research.md`, "the real host ANSWER".

1. **Does the host receive the forward at all?** — **STILL OPEN, and still the
   prerequisite.** This is the only one that is genuinely about the tunnel
   rather than about Blur, so only two machines can settle it. Host mode reports
   `forwards heard` precisely so one live run answers it.
2. Is the host's Blur reply **from the discovery port**? — **YES, confirmed.**
   The answer's source is `50001`, not an ephemeral port.
3. Is the reply **unicast** to the player's address, or broadcast? —
   **UNICAST, confirmed.** Destination was exactly the injected source
   `10.88.14.200:50001`. It never broadcast its answer. This also confirms the
   host *has* to be told which address to reply to, which is why it answers the
   source it saw.
4. Is the reply's **destination port** the player's Blur source port? — **YES,
   confirmed.** `50001 -> 50001`, ports preserved as the clone assumes.
5. Does the host's Blur **answer a forward whose source is a foreign address**
   at all? — **YES for the overlay subnet, confirmed:** it answered a query from
   `10.88.14.200`, which is not a local address. Whether it answers a query from
   a *different* subnet is unresolved; one later run answered nothing at all,
   including on the overlay (see the "NOT yet explained" section of the capture
   doc). Treat "it answers" as proven and "it always answers" as unproven.

**Consequence for the design: nothing in the reply path needs to change on the
strength of these four.** The clone-of-the-answer design is doing exactly what
the real game requires. The open risk moved to the embedded-endpoint question
below, which the capture also settled part of.

Note: no commits — this checkout has no git repository.

## Done (implemented + tested, no live Blur traffic needed)

- [x] Repo skeleton, solution, contracts, core, WPF GUI, native helper
- [x] 220 managed unit/fuzz/concurrency/IPC-framing/view-model tests, all
      passing (was 173 before host mode)
- [x] Portable C++ suite (92,848 checks incl. fuzz + thread hammering), passing
- [x] Helper links clean (`-Werror`) and passes 45 scripted live interop
      checks (`scripts/test-interop.ps1`): status, token auth, protocol
      hello/version gate, unknown command, driver-error path, validation
      path, host-mode lifecycle/refusals, stop/shutdown, watchdog,
      --log-file mirror + fail-fast. The script is also verified from a clean
      build with **no arguments** (its own MinGW fallback), not just with a
      pre-built `-HelperExe`
- [x] WinDivert 2.2 ABI: 24-byte address, impostor/TTL semantics, filter
      primitives verified against official docs. **Correction (2026-09-12): the
      shutdown enum was recorded wrong and was never actually verified** — it is
      bit flags (RECV 0x1, SEND 0x2, BOTH 0x3), not ordinals 0/1/2. That mistake
      wedged every Stop(); see the high-severity entry below. The values are now
      pinned by static_assert in `blurlink/windivert_abi.h` and asserted by the
      native suite.
- [x] Watchdog: helper self-exits when the GUI disconnects (15s default)
- [x] Manifest-proof OS check (`RtlGetVersion`) + asInvoker manifest
- [x] IPC hardening: send serialization, no-BOM UTF-8, connect retry with
      slow-UAC tolerance, short timeouts on stop/poll
- [x] Blur-exit auto-stop watcher (opt-out), helper-lost detection + force kill
- [x] Pre-flight checklist + route details in Join UI
- [x] Metadata-only file logger wired to the logLevel setting
- [x] App icon, version stamping, CI workflow, SECURITY.md
- [x] Helper console dashboard (banner, commands, [fwd]/[heard] lines, shutdown
      counters) + interop asserts incl. token/pipe leak checks
- [x] Stop verification (wait-for-exit, force-kill fallback, honest message)
- [x] Stale-session handling (restart dead-token helpers, drop stale pipe)
- [x] Attach to already-running Blur + status line + auto-stop
- [x] WinDivert 2.2.2 bundled + first-launch auto-staging
- [x] Hardening pass (2026-09-09 review):
      IPC protocol-version gate (hello required, explicit mismatch error);
      shadow-proof JSON key lookup (values can no longer fake fields);
      recv-failure backoff (no hot-spin when the driver misbehaves);
      deterministic window-close stop (close cancels, bridge stops, then
      the window closes);
      settings.json corrupt-file backup (.corrupt) instead of silent reset;
      real payload-leak redaction guard (hex runs / capture markers /
      base64 blobs rejected);
      helper dashboard mirrored to %LocalAppData%\BlurLink\logs\helper.log
      (--log-file, 1MB rotation) and shown in the status bar (wd=Ns)
- [x] Adapter auto-refresh (system events + on navigate), loopback hidden
- [x] Blur launch: game-folder working dir, args field, exit code/duration
- [x] One-click discovery-port detector (SNIFF, bounded, metadata-only)
- [x] Helper log viewer (Settings): tail of helper.log in the GUI with
      refresh/copy/auto-refresh; helper mirrors its dashboard to disk via
      --log-file (1MB rotation) so reports survive the console closing
- [x] Single-instance guard (2026-09-11 review): one GUI window only
      (`SingleInstanceGuard`, named event so "created new" is deterministic and
      a crash leaves no stale lock; a second launch focuses the running window
      or explains itself), plus a machine-global helper-side bridge-session
      event that refuses a concurrent bridge (`another BlurLink bridge is
      already active`) and is released on stop/shutdown/failed start/discovery
      sniff. Interop now covers refusal and release
      (`second-bridge-refused-while-session-held`,
      `start-allowed-after-session-released`, `failed-start-releases-session`)
- [x] High-severity fix pass (2026-09-11 review):
      Start no longer restarts the helper it just started (a successful start
      reply marks the session live, and the automatic reply-listen reuses the
      authenticated client instead of re-entering EnsureHelperAsync);
      Settings Import/Reset now reload the Join tab fields under a write guard
      so a later Start cannot clobber imported values; "Ping host" reports
      reachable only on IPStatus.Success (a timed-out reply is non-null);
      helper dedupSkipped/fragmentsRejected counters are modelled and shown;
      IHelperProcess seam makes the start path testable without elevation
- [x] Medium-severity fix pass (2026-09-11 review, follow-up to the
      high-severity pass):
      a refused automatic reply-listen no longer kills the live bridge — the
      listener is observe-only and rides the session Start just authenticated,
      so only a sniff that launched its own helper still tears it down
      (RefusedAutoReplyListen_LeavesTheLiveBridgeAlone);
      the helper-exit wait is awaited instead of Thread.Sleep, so Stop and
      window-close can no longer freeze the UI for up to 3s
      (StopAsync_WaitsForTheHelperWithoutBlockingTheCallingThread pumps a
      simulated UI context and fails if a continuation blocks it);
      Import/Reset now push the imported/reset log level into the live logger
      (Save was the only path doing it, so the saved setting and the effective
      level silently disagreed) and unknown logLevel values are normalized on
      load/import through one shared canonical list;
      flaky redaction guard fixed: Metadata_ContainsNoPayload asserted payload
      bytes against the wall clock, so it failed ~1 run in 12 whenever the
      timestamp contained "42" — the clock is now pinned to 18:42:07.420 and
      excluded from the check (173 managed tests, green over 6 straight runs)

## Needs live Blur traffic (blocked on a real session)

Reconciled 2026-09-12 against the driver-level runs. Two items below were closed
by *evidence from those runs* rather than by a live session, and are marked as
such so the distinction stays visible — a driver-level proof is not a live
two-machine proof.

- [x] FIRST EVIDENCE (2026-09-09, joiner capture via built-in detector):
      Blur discovery broadcasts to 255.255.255.255:**50001** (4/4 packets)
- [x] Blur binds 0.0.0.0:**50001** (discovery, unicast-receivable) and
      0.0.0.0:**3074** (probable gameplay port) — verified via socket table
- [x] Forward path proven live: `[fwd] joiner:50001 -> broadcast:50001 =>
      host:50001`, captured=forwarded=1, no errors
- [x] ~~A **hosting** Blur is SILENT~~ — **RETRACTED 2026-09-12.** A 20 s
      watch cannot distinguish "never broadcasts" from "broadcasts more slowly
      than the window", and it landed in a gap: the game does query while
      sitting on its LAN lobby screen — twice in a 45 s timestamped window, and
      four times in a 240 s one, **with the host machine untouched** (the user
      created the two lobbies once and clicked nothing afterwards). See
      `docs/packet-research.md` → CORRECTION → "The host's own account of the
      session". The retracted measurement is kept because the mistake is
      instructive: measured live 2026-09-12 with `hostsim capture`
      (observe-only, SNIFF), while this machine hosted a LAN game, 20s of
      unfiltered outbound capture found 71 packets and **none** of them a
      broadcast or on port 50001, and ports 50001/3074 were quiet in both
      directions.
      **What survives:** the host has nothing to *answer* until a query arrives,
      so a host-only capture proves nothing about the reply path, and
      `forwards heard` reading 0 while hosting is still expected. The next live
      step is unchanged — capture a query from a *searching* Blur and replay it
      at the host.
- [ ] Peers currently OFFLINE (ping to .59.224 and .132.54 both time out;
      route + adapter healthy) — rerun when friend is online
- [ ] Confirm with a second listen (stability across refreshes/restarts)
- [ ] Source-port behavior (fixed vs ephemeral — check CMD `[heard]` lines)
- [x] Response direction/ports + embedded-IP verdict — **ANSWERED
      (2026-09-12)** by replaying a real captured query at a hosting Blur. The
      answer is unicast, from the discovery port `50001` to the querying
      `address:50001`; the design's assumptions (a) and (b) are confirmed
      against real game code, not inferred. The embedded-IP question is also
      answered — **YES, the reply embeds the host's own addresses inside the
      payload**, two of them, each followed by the little-endian gameplay port
      `0x0C02` = 3074. What that means for the player is NOT settled (see
      below), so this item answers "does it embed" and leaves "does it matter"
      open. Raw bytes: `docs/packet-research.md`.
- [ ] `profiles/blur-lan-verified-<date>.json`
- [x] Runtime filter acceptance by the real WinDivert driver — **ANSWERED
      (2026-09-12)**, and answered more strongly than it was asked. The
      acceptance question was "does the driver accept the filter strings we
      build?" The elevated harness went further and *acted on* them: the bridge's
      filter (`outbound && ip && udp && udp.DstPort == 50001 && ip.DstAddr ==
      255.255.255.255`) was opened and matched real injected traffic; host mode's
      composite filter — including a handle **reopened with players in it**
      (`(inbound && … 47811) || (inbound && … && (ip.SrcAddr == …)) ||
      (outbound && … && (ip.DstAddr == …))`) — was accepted and then matched the
      reply, wrong-port and broadcast cases separately. Both fail cleanly with a
      reason string when the driver refuses. What an unelevated test could never
      show is now shown.
- [ ] First live **bridge** test — the synthetic half is now provable, the real
      half is not. PROVEN at the driver level (2026-09-12): a Blur-shaped LAN
      discovery broadcast is captured and cloned to the host's overlay address,
      the original is still reinjected, and the join side introduces itself
      (`bridge-announces-itself-to-the-host`). REMAINING, and it needs two
      machines and real Blur: forwards from an actual Blur, the lobby appearing,
      and an actual join. Also unproven end to end: a real host **acting on**
      the introduction — the join side is proven to *send* it, never yet proven
      to be *understood*, because the one-machine session lock (correctly) stops
      two BlurLink instances running host mode and bridge on one PC.
- [ ] Two-machine live session (the authority on everything above) — see
      `docs/capture-day-checklist.md`. **Narrowed on 2026-09-12:** the host's
      reply packets were captured on one machine, so what is left for two
      machines is smaller and sharper than it was. The question is no longer
      "what does the reply look like" but three specific ones: (a) does the
      forward arrive over the tunnel at all (`forwards heard`); (b) which of the
      two embedded endpoints does the player's Blur actually try — the dead
      physical one or the overlay one; (c) does the lobby appear. (b) is the one
      that decides whether payload rewriting is ever needed.

## Possible v2 (only with verified format — see docs)

- [x] Host-side helper rule — **done** (Host mode, above). It was moved out of
      "only if research proves it needed" on 2026-09-11 because the mechanism
      is understood without new research: the host's Blur necessarily replies
      to the physical LAN address it saw on the forward, which it cannot route
      over the overlay. The unproven part is not *whether* the reply needs a
      redirect but whether the forward reaches the host at all (open question
      1) — which Host mode's `forwards heard` counter answers.
- [ ] Narrow the WinDivert filter further with an `ifIdx` term, so the adapter
      pickers actually constrain capture (today they are validated but unused;
      see the observation above)
- [ ] Offset-exact payload reflection for embedded LAN IPs (never blind).
      **Decision point, not a task**, and the 2026-09-12 capture made it *less*
      urgent, not more. The reply does embed the host's addresses (confirmed),
      but the two it lists are `192.168.1.116:3074` (the physical LAN, dead
      across the tunnel) **and `10.88.14.114:3074` (the overlay address, which
      the friend can already reach)**. The message therefore already contains a
      usable endpoint, and rewriting may never be needed. Whether Blur *uses*
      that entry is the question the two-machine session settles; do not build a
      rewriter before it, and do not change the "no payload rewrite, ever" rule
      (a safety promise) without the user's explicit approval.
- [ ] Signed MSIX installer, ARM64 evaluation
