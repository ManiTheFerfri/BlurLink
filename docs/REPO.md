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
- **Portable packaging staging.** `src/BlurLink.Shell/Native/` is where
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

## History note (M4 Task 1 — R1: single-instance mutex keeps its name)

  The single-instance guard stays `Local\BlurLink.Desktop.SingleInstance`
  (`SingleInstanceGuard.DefaultName`), even though the WPF Desktop shell is
  being retired for the Avalonia Shell at 0.3.0. Renaming it would orphan the
  guard during upgrade overlap: an old WPF install and the new Shell running
  side by side would each acquire their own mutex and double-divert. Cost if
  wrong: duplicate forwarding for upgraders. So the `Desktop` in the name is
  history, not a bug — do not "fix" it.

## History note (M4 Task 4 — WPF removed at 0.3.0)

  The WPF shell (`src/BlurLink.Desktop/`) was removed from the solution and
  the disk at 0.3.0: the Avalonia Shell (`src/BlurLink.Shell/`) is the app,
  and the portable pipeline (`scripts/build-portable.ps1`,
  `scripts/package-release.ps1`) publishes it, embedding the helper plus the
  WinDivert runtime from `src/BlurLink.Shell/Native/` exactly as Desktop did.
  Desktop history is retained in git (`git log -- src/BlurLink.Desktop`);
  the `Desktop` in the single-instance guard name stays per the R1 note above.

## Public mirror runbook (M4 Task 6 — doc-only; executes at mirror time)

> Status: DOCUMENTATION ONLY. Writing this section created no remote, tag,
> branch, or push. Every command below runs at mirror time, by the user.
>
> NEVER run this before the user provides the GitHub repository URL.
> NEVER push full history public — only the single-root `public` branch
> built in step 2 leaves the private clone.

Prerequisites: a machine with git, the `gh` CLI (authenticated — see the
"Local verdict" note in `docs/driver-ci-experiment.md` for the last known
auth state), and the GitHub URL for the new public repository (provided by
the user at mirror time; no guessed URL is ever substituted).

Method (R3): the public history RESTARTS at the scrubbed commit `a238ad0`
(first clean commit) as a new single root. Pre-scrub objects stay in the
private clone only; no filter-repo surgery is used.

### 0. Verify the clean commit exists locally

```powershell
git cat-file -t a238ad0
git rev-parse a238ad0
```

Expected (verified 2026-09-21 on `v2-release`):

```text
commit
a238ad09dfbd724aaade9579eaed12cfeeecdee6
```

### 1. Fresh clone (private origin only)

```powershell
git clone https://gitlab.com/blurlink/blurlink.git blurlink-public-prep
cd blurlink-public-prep
git remote -v
```

Expected: clone output ending with `done.`; `git remote -v` shows ONLY the
private origin (fetch + push). No `github` remote exists yet — that is
correct; step 5 creates it.

### 2. Build the single-root `public` branch

Do NOT `git checkout -b public a238ad0`: that keeps the two pre-scrub
ancestors (`dca7ef0`, `8216dbb`) on the branch, and their trees contain the
banned map — pushing that branch would publish the very identifiers the
scrub removed (verified 2026-09-21: every banned literal hits those
ancestor trees, while the `a238ad0` tree itself hits only the checker
file). Instead, restart the history with an orphan root carrying the clean
tree:

```powershell
git checkout --orphan public a238ad0
git commit -C a238ad0
git rev-parse HEAD
git rev-list --count HEAD
git log --oneline
```

Expected: `git commit` prints `[public (root-commit) <new-hash>] Scrub
identifying literals from docs and scripts, and check them in CI-able
form`. `<new-hash>` DIFFERS from `a238ad0` — that is the point: a new root
with the clean tree and no parents. `git rev-list --count HEAD` prints
`1`. `git log --oneline` prints exactly one line (the reused scrub
message). (Predicted outputs — this runbook was written doc-only and these
steps were not executed here; confirm each line at mirror time.)

### 3. Log proof

```powershell
git log --oneline -5
```

Expected: the single scrub-message line from step 2 — nothing else. Any
additional line means the branch carries history it must not; stop and
rebuild step 2.

### 4. Banned-map grep proof

The exact banned literals live ONLY in `scripts/check-docs-privacy.ps1`
(the checker skips itself and `docs/privacy-scrub.md`; pasting them into
any other file — including this runbook — would itself trip the checker,
so they are derived from the script here, never quoted):

```powershell
$lits = @(Select-String -LiteralPath scripts/check-docs-privacy.ps1 -Pattern "^\s*'([^']+)'\s*=" -AllMatches |
  Select-Object -ExpandProperty Matches |
  ForEach-Object { $_.Groups[1].Value })
$lits.Count
foreach ($lit in $lits) {
  $hits = git grep -n -F -e $lit -- . ':!scripts/check-docs-privacy.ps1' ':!docs/privacy-scrub.md'
  if ($hits) { $hits; throw 'banned literal present outside the checker' }
}
$npats = @(Select-String -LiteralPath scripts/check-docs-privacy.ps1 -Pattern "\$line -cmatch '([^']+)'" -AllMatches |
  Select-Object -ExpandProperty Matches |
  ForEach-Object { $_.Groups[1].Value })
foreach ($p in $npats) {
  $hits = git grep -n -P -e $p -- . ':!scripts/check-docs-privacy.ps1' ':!docs/privacy-scrub.md'
  if ($hits) { $hits; throw 'name-pattern hit outside the checker' }
}
Write-Host 'grep-proof-ok'
```

Expected: `$lits.Count` prints the map size (9 in the public tree — the
tenth, broadcast entry was added to the private tip later; see the union
check below); the loops print nothing and throw nothing; the `-P` greps
(the operator-first-name word-boundary check, pattern likewise derived,
never pasted) print nothing; `grep-proof-ok`. If `git grep -P` is
unsupported on the mirror machine, the checker run below is the
authoritative gate for the name check. Then run the authoritative gate if
PowerShell 7 is available:

```powershell
pwsh -NoProfile -File scripts/check-docs-privacy.ps1
```

Expected: `privacy-ok`, exit 0.

Union check (map drift guard): the private tip may know a newer map than
the public tree's copy (today: one extra broadcast literal added after
`a238ad0`). Repeat the loop against the union of both maps before pushing:

```powershell
git show v2-release:scripts/check-docs-privacy.ps1 > $env:TEMP/checker-tip.ps1
$tip = @(Select-String -LiteralPath $env:TEMP/checker-tip.ps1 -Pattern "^\s*'([^']+)'\s*=" -AllMatches |
  Select-Object -ExpandProperty Matches |
  ForEach-Object { $_.Groups[1].Value })
$all = @($lits) + @($tip | Where-Object { $lits -notcontains $_ })
$all.Count
foreach ($lit in $all) {
  $hits = git grep -n -F -e $lit -- . ':!scripts/check-docs-privacy.ps1' ':!docs/privacy-scrub.md'
  if ($hits) { $hits; throw 'banned literal present outside the checker' }
}
Remove-Item $env:TEMP/checker-tip.ps1
Write-Host 'union-proof-ok'
```

(Replace `v2-release` with the private branch you cloned if different.)
Expected: `$all.Count` prints the union size (10 today); no hits;
`union-proof-ok`. (The `a238ad0` tree was verified clean against the full
current 10-literal map on 2026-09-21, so this passes — it guards against a
future map addition landing between now and mirror time.)

### 5. Add the GitHub remote and push ONLY `public`

```powershell
git remote add github <paste-the-URL-the-user-provided>
git remote -v
git push github public:main
git ls-remote github
```

Expected: `git remote -v` shows `origin` (private) plus `github` (the user
URL). Push prints `* [new branch]      public -> main`. `git ls-remote
github` lists ONLY `refs/heads/main` — no tags, no other branches.
(Predicted outputs — confirm at mirror time.) This pushes exactly one
commit with the clean tree: the full private history never leaves the
private clone.

### 6. Trigger the driver experiment on the mirror

From a checkout of the new GitHub repository (or append
`--repo <owner>/<repo>` to each `gh` call):

```powershell
gh workflow run driver.yml --ref main
gh run list --workflow=driver.yml
```

Expected: the first command exits 0; the list then shows the
queued/dispatched run. Watch it (`gh run watch <run-id>`), then fetch the
evidence:

```powershell
gh run download <run-id> -n driver-run-log
```

### 7. Run the release workflow for 0.3.0

```powershell
gh workflow run release.yml --ref main -f version=0.3.0
gh run list --workflow=release.yml
```

Expected: exit 0 and a queued run. (The workflow rebuilds the package on
`windows-latest` and uploads the `BlurLink-release` artifact; the
tag-triggered release attach is unchanged — tags still go to the private
origin only with user approval, per Task 5.)

### 8. Fill in `docs/driver-ci-experiment.md`

Back on the working branch, replace the PENDING verdict using ONLY the
downloaded artifact:

- Runner image: the resolved image from the run's "Set up job" step.
- Justifying output lines: quote verbatim — the 42-check summary plus the
  `harness-exit=0` marker (`runs`); or the `WinDivertOpen` refusal /
  `CANNOT RUN:` lines plus marker (`cannot-run`); or the `FAIL` lines plus
  the trailing helper dashboard (`fails`).
- Consequence: follow the branch the verdict selects (permanent harness
  home / R5 fallback / investigate). Never record a verdict without the
  artifact lines above it.
