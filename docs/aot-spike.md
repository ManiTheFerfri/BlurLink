# NativeAOT spike — experiment record (Task 11)

Spec D10: the result is recorded, the default deliverable does not change.
The shipping flavor stays **slim framework-dependent**
(`scripts/build-portable.ps1`, ~3.5 MB single `BlurLink.exe`, needs the
desktop runtime). This spike exists only to measure the alternative.

The experiment script is `scripts/build-aot.ps1`: it publishes
`src/BlurLink.Desktop` with `-p:PublishAot=true` to `dist/BlurLink-Aot/`,
prints size in MB and cold-start time in ms, and exits non-zero when the
publish fails.

## Provenance — read this before quoting any number

- **WPF numbers below are the pre-port baseline (this task).** The current
  `src/BlurLink.Desktop` is a WPF app (`<UseWPF>true</UseWPF>`,
  `net10.0-windows`). `PublishAot` on it is **expected to fail** with
  `NETSDK1168` — that failure is the recorded datum, not a task failure.
- **Avalonia numbers do not exist yet.** They are recorded here only after
  Task 4 of the surface plan lands the Avalonia port; re-run
  `scripts/build-aot.ps1` against that tree and fill in the second row.

## Results

| Flavor | Size | Cold start (window) | Helper staging (`EnsureStagedBinaries`) | UAC launch | What failed |
| --- | --- | --- | --- | --- | --- |
| Slim framework-dependent (default, `build-portable.ps1`) | ~3.5 MB | not measured here | works (unchanged path) | works (unchanged path) | — (shipping flavor) |
| NativeAOT on **WPF** (baseline, 2026-09-14) | n/a — no binary produced | n/a — no binary produced | not reached (no binary to stage from) | not reached (nothing to launch) | `NETSDK1168`: WPF + trimming unsupported (see verbatim error) |
| NativeAOT on **Avalonia** (after surface-plan Task 4) | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |

Slim comparison: the AOT experiment produced nothing to compare yet. If the
Avalonia re-run succeeds, its size row is judged against the slim ~3.5 MB
baseline (self-contained single file vs framework-dependent single file —
different trade, recorded side by side, not seamlessly interchangeable).

## Verbatim WPF baseline run (2026-09-14, SDK 10.0.401)

Command: `pwsh -NoProfile -File scripts/build-aot.ps1` (defaults,
`Release`). Exit code: **1**. `dist/BlurLink-Aot/` was not created.

```text
Determining projects to restore...
Restored <repo>/src/BlurLink.Core/BlurLink.Core.csproj (in 34.07 sec).
Restored <repo>/src/BlurLink.Contracts/BlurLink.Contracts.csproj (in 34.07 sec).
Restored <repo>/src/BlurLink.Desktop/BlurLink.Desktop.csproj (in 34.07 sec).
C:\Program Files\dotnet\sdk\10.0.401\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.RuntimeIdentifierInference.targets(307,5): error NETSDK1168: WPF is not supported or recommended with trimming enabled. Please go to https://aka.ms/dotnet-illink/wpf for more details. [<repo>/src/BlurLink.Desktop/BlurLink.Desktop.csproj]
Exception: <repo>/scripts/build-aot.ps1:18
Line |
  18 | if ($LASTEXITCODE -ne 0) { throw 'AOT publish failed' }
     |                            ~~~~~~~~~~~~~~~~~~~~~~~~~~
     | AOT publish failed
```

(Local checkout paths redacted to `<repo>` per `docs/privacy-scrub.md`;
error text itself is verbatim.)

## Verdict

- **WPF + NativeAOT: not viable** — the SDK refuses the combination
  (`NETSDK1168`), before any code of ours runs. No size, startup, staging,
  or UAC data can exist for this flavor.
- The publish did **not** unexpectedly succeed, so there was nothing further
  to measure honestly (per the brief: no forced outcome either way).
- Nothing else changed: no project file was touched, `build-portable.ps1`
  and the default deliverable are exactly as before.
- Next step: after the Avalonia port (surface-plan Task 4), re-run
  `scripts/build-aot.ps1` unmodified and fill in the Avalonia row —
  size, window time, staging/UAC result, and whatever failed, if anything.
