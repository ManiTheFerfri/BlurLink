# Privacy scrub record (Task 2)

**Rule (spec §8.1, D7):** structure, offsets and byte layout stay; identifiers
are replaced. This record describes each scrubbed literal by **kind** — live
exact identifiers are intentionally not quoted here (quoting them would
reintroduce them). The exact banned map lives only in
`scripts/check-docs-privacy.ps1`, which the checker itself skips.

## Mapping (kind → replacement)

| Literal kind | Replacement (scrubbed value in tree) | Why |
|---|---|---|
| Author overlay addresses (two, on the author's /24) | `10.0.0.10`, `10.0.0.200` | Author's real overlay IPs; last octet preserved for the second |
| Author physical LAN addresses (two) | `192.168.0.10`, `192.168.0.200` | Author's real LAN IPs; moved to documentation `.0` subnet |
| Author overlay network name (product name, seen with a `-LAN` suffix in one place) | `Example-Overlay` (suffix preserved, e.g. `Example-Overlay-LAN`) | Author's overlay product name |
| Author host name inside captured reply-payload hex (UTF-16LE, length-preserving) | `48006f0073007400` (UTF-16 `Host`; in tables shown split with the trailing null exactly as the original was) | Operator host name embedded in the 160-byte reply excerpt |
| Author overlay address inside payload hex (4-byte LE encoding at the endpoint-list offset) | `0a00000a` (decodes to `10.0.0.10`) | Same address as above, in raw-hex form; offsets intact |
| Author LAN address inside payload hex (4-byte LE encoding at the endpoint-list offset) | `c0a8000a` (decodes to `192.168.0.10`) | Same address as above, in raw-hex form; offsets intact |
| Author game process id (5 digits) | `12345` | Real Blur PID from the live session; digit count preserved |
| Author machine name (standalone word only) | `Author` in generic prose; `Host` where the word directly labels the payload hex (so the label still decodes true) | Operator first name / host name; word-boundary match only, so `Manifest` and similar never match |
| Author adapter names | `Ethernet`, `Example-Overlay` | Sweep found no live adapter-name hits in `scripts/test-e2e.ps1` or `tools/hostsim/hostsim.cpp` (comments/strings checked); replacements reserved by the rule |

Hex length rule: replacements are length-preserving. The 4-character host-name
hex stays 4 characters (`Host`); the 4-byte IP runs stay 4 bytes; the reply
excerpt keeps its 160-byte layout and every documented offset. The payload line
wrapping is unchanged (the second endpoint run spans the same two lines as
before, with each half replaced in place).

## Discovery (Step 2)

Human discovery used a broad prefix search for the author's /24s across tracked
text files, plus the exact banned map in the checker for the overlay product
name, game PID, operator name and payload hex:

```bash
grep -rn "10\.88\.14\.\|192\.168\.1\." --include='*.md' --include='*.ps1' --include='*.cpp' --include='*.json' . | grep -v '^./out/' | grep -v '^./bin/'
# plus exact-literal checks via scripts/check-docs-privacy.ps1 (exact author
# identifiers only, not prefixes — prefixes collide with synthetic fixtures).
```

Exact literals are intentionally not repeated in this command: they live only
in the checker (skipped by itself). The enforced checker bans exact literals
only; the broad prefixes above remain the human discovery tool.

## Files scrubbed

- `docs/packet-research.md`: overlay/LAN addresses, overlay-name suffix,
  game PID, all three payload-hex runs (host name + both endpoint IPs, including
  the split-across-lines run replaced in place), decoded host-name label, and
  the subnet-directed broadcast address moved to the replacement `/24`'s
  broadcast for consistency. Synthetic `.201`/`210` shorthands and telemetry
  addresses left (not author identifiers).
- `TODO.md`: game PID (three spots), overlay request/answer addresses, both
  endpoint-list addresses.
- `docs/superpowers/specs/2026-09-12-v2-design.md`: both endpoint addresses in
  §5.2, host-name + payload hex in §8.1 (name now matches its hex decoding).
- `docs/superpowers/plans/2026-09-12-v2-foundation-and-engineering.md`: Task 2
  table converted to kind-only; intro hex, grep example and checker snippet
  scrubbed to placeholders pointing at the checker; deliberate-violation example
  described without quoting a live literal.
- `README.md`, `docs/user-guide.md`, `docs/capture-day-checklist.md`,
  `docs/troubleshooting.md`: swept — no exact author identifiers found, no
  changes.
- `scripts/test-e2e.ps1`, `tools/hostsim/hostsim.cpp`: swept for the operator
  first name and author adapter names in comments/strings — no hits, no
  behavior change.

## Checker

`scripts/check-docs-privacy.ps1`: no parameters; prints `privacy-ok` and exits
0, or prints each violation as `<file>:<line>: <literal> (<reason>)` and exits
1. Scans the listed source extensions, skips build/output/third-party dirs plus
the git-ignored SDD scratch dir, and skips itself and this record. The operator
name uses a word-boundary match so build words containing that substring never
match. Wired into CI by Task 3.
