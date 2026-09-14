# Repository contents (REPO.md)

BlurLink is a Windows-only .NET 8 + C++ WinDivert bridge for the game
Blur's LAN discovery. This file records what is and is not in version
control, and why.

## Versioned

- `src/` — application, helper, detector and test sources.
- `scripts/` — build, packaging, verification and harness scripts.
- `profiles/` — Wireshark/dissector or capture profiles (text config only).
- `docs/` — specs, plans and records, including
  `docs/superpowers/specs/2026-09-12-v2-design.md` (V2 design spec) and
  `docs/superpowers/plans/2026-09-12-v2-foundation-and-engineering.md`
  (V2 foundation/engineering plan).
- `third-party/WinDivert/README.md` and `LICENSE.Windivert` — the pinned
  version pointer and licence text (not the binaries themselves).

## Never committed

- **Packet captures (`*.cap`) and logs (`*.log`).** Captures can contain
  real traffic excerpts, so they must never enter history. `out/` holds
  real packet captures from research/harness runs and is ignored for
  that reason.
- **Third-party binaries.** The WinDivert driver and DLL
  (`WinDivert.dll`, `WinDivert64.sys`) are downloaded at build time and
  hash-verified, never committed. The pinned version (2.2.2, x64) and
  the expected SHA256 hashes live in
  `third-party/WinDivert/README.md`; the ignored local drop folders are
  `third-party/WinDivert/x64/` (plus `x86/`, `aarch64/`).
- **Build output.** `bin/`, `obj/`, `out/`, `dist/`, `build/` (and the
  existing root-anchored `/build-native/`, `/dist/`, `publish/`,
  `TestResults/`, packaging artefacts `*.msi`/`*.msix`/`*.zip`) are
  generated artefacts. `dist/` in particular is build output.
- **Portable packaging staging.** `src/BlurLink.Desktop/Native/` is where
  the helper plus WinDivert are embedded at build time; it is staged,
  never committed.
- **Editor and per-user noise.** `.vs/`, `.vscode/`, `*.user`,
  `*.vcxproj.user`, `*.suo` and OS junk.
- **SDD working scratch.** `.superpowers/sdd/` is self-ignored via
  `.superpowers/sdd/.gitignore` and never enters history.

## History note (Task 1 reconcile)

The V2 plan assumed a fresh repository, but the repo already existed
(3 commits on `main`, full `.gitignore`, CI present), so Task 1 kept
history and worked on branch `v2-foundation`: the existing `.gitignore`
was merged, never replaced (every prior line kept; only the missing
plan-listed entries were appended), this file was added, and the commit
records the REPO.md + ignore reconcile — the V2 spec and plan files
were already tracked, so there was nothing new to add for them.
