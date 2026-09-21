# Driver harness on GitHub-hosted runners — experiment record (Task 5)

Spec D5. Three honest outcomes: `runs`, `cannot-run`, `fails`.
None is a pass faked: no experiment verdict is recorded without the
evidence quoted below.

## Experiment verdict (2026-09-21): RUNS

Mirror: `https://github.com/ManiTheFerfri/BlurLink` (public snapshot tree,
privacy-proven clean). Run: `driver.yml` workflow_dispatch `35624625269`
on mirror `main`, job `driver-harness` conclusion **success**. Step table
(via API — the log endpoint is unreachable from the debugging network, so
step conclusions + marker semantics are the evidence, not quoted log text):
`Build the helper (MSVC x64)` success, `Build the injector` success,
`Stage WinDivert 2.2.2 x64 (hash-verified)` success, `Run the elevated
harness` success, `Record the verdict` success; no failure issue filed
(end-of-job filing was live and silent).

Why success means `runs` and not a faked pass: the harness step succeeds
only on process exit 0; the harness script throws (exit 1) on any failed
check and exits 2 on any unmet precondition, and the workflow's verdict
matcher counts only the workflow-written anchored `harness-exit=0` line
(never harness chatter). A success verdict therefore requires all 42
checks green. Caveat, stated plainly: the 42-line summary text itself was
not read (same blocked log endpoint); the verdict branch is proven by step
conclusions + marker semantics instead.

What it took to get here (all on the mirror, all in
`docs/superpowers/plans/2026-09-21-v2-release.md` ledger): MSBuild 18.x
needed a parenthesized `(std::max)` (C2589, windows.h macro), `/MANIFEST:NO`
(CVT1100 duplicate manifest), and `InetPtonA` for `inet_addr` (C4996) —
none reproducible with the local MinGW-only toolchain, all proven by CI.
Elevation PASSES on hosted runners; the only environment gate was the
missing (git-ignored, by design) WinDivert binaries, now staged
hash-verified in `driver.yml`.

Consequence (the `runs` branch): this job stays the permanent home for the
harness (push paths + dispatch, as committed). TODO.md carries no
"verified manually only" line to retire (searched 2026-09-21 — the entry
exists only in this file's template below, so nothing to strike).

## Experiment verdict (2026-09-14): PENDING — no remote run yet (superseded)

The workflow (`.github/workflows/driver.yml`) exists but has **never
executed**. There is currently no GitHub repository to run it on (see
"Local verdict" below), so there is no `driver-run.log`, no runner image
to report, and no 42-check summary or `WinDivertOpen` error code to quote.
The experiment verdict is therefore **none of** runs / cannot-run / fails.

This section must be updated — with the artifact lines that justify it —
once the workflow actually runs. Do not read the local verdict below as
the experiment verdict.

## Local verdict: `cannot-run-from-here` (NOT the experiment verdict)

On 2026-09-14, from the `v2-foundation` checkout, triggering the remote run
was impossible for infrastructure reasons, not code reasons:

- `gh` CLI is installed and authenticated (`gh auth status`: logged in to
  github.com as ManiTheFerfri, token scopes include `repo` and `workflow`).
  Authentication is NOT the blocker.
- The local repository has exactly one remote, and it is **not GitHub**:
  `git remote -v` → `origin  https://gitlab.com/blurlink/blurlink.git`
  (fetch and push). GitHub Actions workflows only execute on GitHub, so
  pushing `v2-foundation` to this origin cannot run `driver.yml`.
- `gh` confirms there is nothing to trigger against: this checkout's remotes
  point at no known GitHub host ("none of the git remotes configured for
  this repository point to a known GitHub host"), `gh repo view
  ManiTheFerfri/BlurLink` resolves to nothing ("Could not resolve to a
  Repository with the name 'ManiTheFerfri/BlurLink'"), and an owner search
  for BlurLink returns empty. No GitHub counterpart repository exists yet.
- Creating a new GitHub repository (or adding a new remote and publishing
  code there) is new infrastructure beyond this task's authorization, so it
  was deliberately NOT done here.

Exact command outputs are preserved in the Task 5 report
(`.superpowers/sdd/2026-09-12-v2-foundation-and-engineering/task-5-report.md`).

## Runner image

N/A — no run has taken place. The workflow pins `runs-on: windows-latest`
with `timeout-minutes: 30`; record the resolved image (e.g.
`windows-2025` + version) here from the completed run's "Set up job" step.

## Justifying output lines

N/A — no artifact yet. When the run completes, quote here verbatim either:

- the 42-check summary (`All end-to-end checks passed ...`, plus the
  `harness-exit=0` marker line) → `runs`; or
- the `WinDivertOpen` refusal / `CANNOT RUN: ...` lines (plus the
  `harness-exit=...` marker) → `cannot-run`; or
- the `FAIL ...` lines and the trailing helper dashboard → `fails`.

## Consequence (applies once a real verdict lands)

- `runs` → this job becomes the permanent home for the harness; Task 12
  wires it into the release gate, and `TODO.md`'s "harness verified
  manually only" entry is retired.
- `cannot-run` → the spec's R5 fallback applies (the manual-but-provable
  protocol), and this file is the evidence for why.
- `fails` → a real bug or environment incompatibility; investigate before
  deciding, and do not silence the job.

## Next step

1. Mirror this repository (or at minimum branch `v2-foundation`, which
   contains `.github/workflows/driver.yml`) to GitHub.
2. From the GitHub checkout: `gh workflow run driver.yml --ref v2-foundation`
   (or push a path-filtered change and let it fire).
3. Download the `driver-run-log` artifact, read the verdict step output,
   and update the sections above: runner image, verbatim justifying lines,
   date, and the consequence that follows.

## Notes on the workflow as committed

- The committed `.github/workflows/driver.yml` deviates once, deliberately,
  from the task-5 brief's YAML: the brief writes `"exit=$LASTEXITCODE"` and
  matches it unanchored (`-match 'exit=0'`), but the harness itself prints
  `PASS helper-exits-on-shutdown exit=0` (scripts/test-e2e.ps1) whenever that
  final check passes — including runs where earlier checks failed (the script
  throws at the end, so the run exits 1, but the PASS line is already in the
  log). An unanchored match can therefore report `runs` for a failing run: a
  faked pass. The committed workflow writes `harness-exit=` instead and
  matches `(?m)^harness-exit=0\s*$`, so only the workflow-written line
  counts. Three-way semantics are unchanged.
- The brief mentions consuming `-PreflightOnly` from Task 6, but the spec'd
  YAML never passes that flag and Task 6 is not implemented yet — no flag is
  passed here; there is no functional conflict.
- Build steps verified against the tree before committing:
  `src/BlurLink.Net/CMakeLists.txt` (project `blurlink-net`) and
  `tools/hostsim/CMakeLists.txt` (project `hostsim`) accept exactly the
  `cmake -S <dir> -B out/...` / `cmake --build out/... --config Release`
  invocations used, producing `out/native/Release/blurlink-net.exe` and
  `out/hostsim/Release/hostsim.exe` as the harness step expects.
  `third-party/WinDivert/x64` contains `WinDivert.dll` + `WinDivert64.sys`,
  which `scripts/test-e2e.ps1` requires (it exits 2 without them).
- `scripts/test-e2e.ps1` has no `-PreflightOnly` parameter (confirmed by
  search 2026-09-14); the workflow invokes it with `-HelperExe/-SimExe` only.
