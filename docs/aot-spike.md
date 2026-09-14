# NativeAOT spike — experiment record (Task 11)

Spec D10: the result is recorded, the default deliverable does not change.
The shipping flavor stays **slim framework-dependent**
(`scripts/build-portable.ps1`, ~3.5 MB single `BlurLink.exe`, needs the
desktop runtime). This spike exists only to measure the alternative.

The experiment script is `scripts/build-aot.ps1`: it publishes the project
given by its optional `-Project` parameter (`src/BlurLink.Desktop` by
default, so default behavior is still the WPF baseline) with
`-p:PublishAot=true` to `dist/BlurLink-Aot/`, prints size in MB and
cold-start time in ms, and exits non-zero when the publish fails.

## Provenance — read this before quoting any number

- **WPF numbers below are the pre-port baseline (this task).** The current
  `src/BlurLink.Desktop` is a WPF app (`<UseWPF>true</UseWPF>`,
  `net10.0-windows`). `PublishAot` on it is **expected to fail** with
  `NETSDK1168` — that failure is the recorded datum, not a task failure.
- **Avalonia numbers below are the Shell re-run (this task, 2026-09-14,
  SDK 10.0.401).** Command:
  `pwsh -NoProfile -File scripts/build-aot.ps1 -Project src/BlurLink.Shell`.
  All four Shell destinations render (surface-plan parity), so the tree
  measured is the real Shell, not a skeleton. Like the WPF row, the
  failure below is the recorded datum, not a task failure.

## Results

| Flavor | Size | Cold start (window) | Helper staging (`EnsureStagedBinaries`) | UAC launch | What failed |
| --- | --- | --- | --- | --- | --- |
| Slim framework-dependent (default, `build-portable.ps1`) | ~3.5 MB | not measured here | works (unchanged path) | works (unchanged path) | — (shipping flavor) |
| NativeAOT on **WPF** (baseline, 2026-09-14) | n/a — no binary produced | n/a — no binary produced | not reached (no binary to stage from) | not reached (nothing to launch) | `NETSDK1168`: WPF + trimming unsupported (see verbatim error) |
| NativeAOT on **Avalonia** (Shell re-run, 2026-09-14) | n/a — no binary produced | n/a — no binary produced | not reached (no binary to stage from) | not reached (nothing to launch) | `IL2026`/`IL3050`: reflection-based `System.Text.Json` in `BlurLink.Core` breaks trimming/AOT (see verbatim error) |

Slim comparison: neither AOT flavor produced a binary, so there is still
nothing to compare. If a future re-run succeeds, its size row is judged
against the slim ~3.5 MB baseline (self-contained single file vs
framework-dependent single file — different trade, recorded side by side,
not seamlessly interchangeable).

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

## Verbatim Avalonia (Shell) run (2026-09-14, SDK 10.0.401)

Command: `pwsh -NoProfile -File scripts/build-aot.ps1 -Project src/BlurLink.Shell`.
Exit code: **1**. `dist/BlurLink-Aot/` was not created. Because no binary
was produced, helper staging (`%LocalAppData%\BlurLink\bin`) and the UAC
elevation prompt were **not reached** — no values are claimed for them.
(Error order varies run to run — a second run emitted the same 16 errors
in a different order; the block below keeps first-run order. Path
separators are normalized to `/`, as in the WPF row.)

```text
Determining projects to restore...
Restored <repo>/src/BlurLink.Core/BlurLink.Core.csproj (in 224 ms).
Restored <repo>/src/BlurLink.Contracts/BlurLink.Contracts.csproj (in 224 ms).
Restored <repo>/src/BlurLink.Platform/BlurLink.Platform.csproj (in 224 ms).
Restored <repo>/src/BlurLink.Shell/BlurLink.Shell.csproj (in 229 ms).
BlurLink.Contracts -> <repo>/src/BlurLink.Contracts/bin/Release/net10.0/BlurLink.Contracts.dll
<repo>/src/BlurLink.Core/Config/GameProfileStore.cs(18,16): error IL2026: Using member 'System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/GameProfileStore.cs(23,17): error IL2026: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/GameProfileStore.cs(23,17): error IL3050: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/GameProfileStore.cs(18,16): error IL3050: Using member 'System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(74,20): error IL2026: Using member 'System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(36,23): error IL2026: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(74,20): error IL3050: Using member 'System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(36,23): error IL3050: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Session/SessionCoordinator.cs(101,22): error IL2026: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Session/SessionCoordinator.cs(101,22): error IL3050: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(102,12): error IL2026: Using member 'System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(102,12): error IL3050: Using member 'System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(106,19): error IL2026: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Config/BlurLinkConfigStore.cs(106,19): error IL3050: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Session/SessionCoordinator.cs(194,17): error IL2026: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
<repo>/src/BlurLink.Core/Session/SessionCoordinator.cs(194,17): error IL3050: Using member 'System.Text.Json.JsonSerializer.Deserialize<TValue>(String, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications. [<repo>/src/BlurLink.Core/BlurLink.Core.csproj]
Exception: <repo>/scripts/build-aot.ps1:21
Line |
  21 | if ($LASTEXITCODE -ne 0) { throw 'AOT publish failed' }
     |                            ~~~~~~~~~~~~~~~~~~~~~~~~~~
     | AOT publish failed
```

(Local checkout paths redacted to `<repo>` per `docs/privacy-scrub.md`;
error text itself is verbatim. The staging/UAC steps were not run because
there is no AOT exe to run them from — per the task rules, nothing is
claimed for them.)

## Verdict

- **WPF + NativeAOT: not viable** — the SDK refuses the combination
  (`NETSDK1168`), before any code of ours runs. No size, startup, staging,
  or UAC data can exist for this flavor.
- The WPF publish did **not** unexpectedly succeed, so there was nothing
  further to measure honestly (per the brief: no forced outcome either way).
- **Avalonia (Shell) + NativeAOT: not viable as-is** — further along than
  WPF (restore + compile succeed; Avalonia itself is AOT-compatible), but
  the publish fails on 16 `IL2026`/`IL3050` errors, all from
  reflection-based `System.Text.Json` calls in `BlurLink.Core`
  (`Config/GameProfileStore.cs`, `Config/BlurLinkConfigStore.cs`,
  `Session/SessionCoordinator.cs`). The fix — source-generated JSON
  (`JsonSerializerContext`) — is real work and out of scope for this
  recording task. No size, startup, staging, or UAC data can exist for
  this flavor either.
- Standing decision, restated (spec D10): the shipping flavor stays
  **slim framework-dependent** (`scripts/build-portable.ps1`); this spike
  changes nothing about the default deliverable. Retiring `-Full`/`-Sfx`
  (D12) needs a usable no-runtime flavor, which neither AOT run produced —
  that retirement belongs to the M4 plan, not this one.
- Nothing else changed: no project file was touched (the script gained an
  additive `-Project` parameter; `build-portable.ps1` and the default
  deliverable are exactly as before).
