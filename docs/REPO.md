# Repository contents (REPO.md)

BlurLink is a Windows-only .NET 10 + C++ WinDivert bridge for the game
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

## Cutting a release (Task 12)

Releases are unsigned on purpose (V2 decision D6): Windows SmartScreen
will warn on first launch — say so in the release notes rather than
implying it does not happen.

1. Bump the version in `Directory.Build.props` (`AssemblyVersion`,
   `FileVersion`, `InformationalVersion`) so the exe `FileVersion`
   matches the release version. The packaging script refuses to
   continue on a mismatch.
2. Run `pwsh -NoProfile -File scripts/package-release.ps1 -Version <semver>`
   (e.g. `0.2.0`). It rebuilds the portable exe via
   `scripts/build-portable.ps1`, stages `BlurLink.exe` + `LICENSE` +
   `THIRD-PARTY-NOTICES.md` into `dist/release/staging/`, writes
   `SHA256SUMS.txt` (one SHA256 line per file), and zips the result to
   `dist/release/BlurLink-<version>-win-x64.zip`, printing the zip's
   SHA256.
3. Verify: unzip and confirm three files plus `SHA256SUMS.txt` (hashes
   match), launch `BlurLink.exe` once and confirm it stages the helper
   plus the WinDivert runtime to `%LocalAppData%\BlurLink\bin`.
4. Tag `v<version>` and push the tag. `.github/workflows/release.yml`
   (also runnable via `workflow_dispatch` with a `version` input)
   rebuilds the package on `windows-latest`, uploads it as the
   `BlurLink-release` artifact, and attaches `dist/release/*` to the
   release for tags.

`dist/` (including `dist/release/`) is git-ignored build output and is
never committed; only the script, the workflow, and this section are
versioned.

## History note (Task 1 reconcile)

The V2 plan assumed a fresh repository, but the repo already existed
(3 commits on `main`, full `.gitignore`, CI present), so Task 1 kept
history and worked on branch `v2-foundation`: the existing `.gitignore`
was merged, never replaced (every prior line kept; only the missing
plan-listed entries were appended), this file was added, and the commit
 records the REPO.md + ignore reconcile — the V2 spec and plan files
 were already tracked, so there was nothing new to add for them.

 Public history will be squashed/restarted from the scrubbed commit
 `a238ad0`: identifiers were scrubbed from the tree there but remain in
 earlier git objects, so the squash gates the public GitHub push and is
 executed at mirror time, verified by a clean-clone grep for the banned
 map in `scripts/check-docs-privacy.ps1`. No history rewrite happens
 before then.
