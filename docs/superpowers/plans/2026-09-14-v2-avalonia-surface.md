# V2 Avalonia Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WPF shell with a modern Avalonia 12 shell over the tested session state model, reaching feature parity plus the spec's first-run, verify-profile, support-bundle, polish and correctness bundles.

**Architecture:** A new `BlurLink.Shell` project (Avalonia 12, `net10.0-windows`) lives beside the untouched WPF `BlurLink.Desktop` until parity; UI-free services move to a new `BlurLink.Platform` project both shells reference. New Shell view-models are thin projections of `SessionState`/`SessionStory` (never owners of session logic), the 1432-line `JoinViewModel` monolith splits into session/settings/sniff parts behind a thin facade, and every view gets headless UI tests. `Contracts`, `Core` logic, and the native helper change only where a task says so.

**Tech Stack:** .NET 10 LTS (`net10.0-windows`), Avalonia 12.1.2 (re-check latest stable 12.x at Task 1 and record), C# with `Nullable` and warnings-as-errors (inherited from `Directory.Build.props`), xUnit 2.9.x + `Avalonia.Headless.XUnit` (test-only), PowerShell 7 scripts, CMake/MSVC native helper unchanged except Task 12, GitHub Actions on `windows-latest`.

**Spec:** `docs/superpowers/specs/2026-09-12-v2-design.md` (this plan implements §6.3, §6.4, §6.5's Avalonia half, §7.1–§7.6, §8.2's UI job, §8.3's headless layer, and §9's M3).

## Global Constraints

Copied from the spec and the M0/M2 stage; every task's requirements implicitly include these.

- **Framework floor:** .NET 10 LTS. `net10.0` for `BlurLink.Platform`, `net10.0-windows` for `BlurLink.Shell` and the test project (already `net10.0-windows`).
- **Windows only.** The platform seam exists for testability, not portability — no other OS target, no portability work (D3).
- **New runtime dependencies, allowed once:** `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, all pinned to the same 12.x version chosen in Task 1. No DI container, no MVVM toolkit (D8). Test-only packages allowed: `Avalonia.Headless`, `Avalonia.Headless.XUnit`.
- **No payload rewriting** (gate closed); nothing touches packet contents except Task 12's observe-only counters, which never drop and never block.
- **No telemetry, no accounts, no backend, no network listener.** Diagnostics stay metadata-only; the redaction rule (no payload hex, no tokens, no pipe names in logs/bundles) is not weakened anywhere. Test fixtures use synthetic payloads at real lengths (24/160 bytes) — never paste real capture hex (privacy rule).
- **WinDivert 2.2.2 stays**; the native helper changes only in Task 12 (additive `ifIdx` term + additive observe-only counters).
- **Unsigned for now.** No signing, no MSIX, no auto-update.
- **Slim WPF framework-dependent stays the deliverable and WPF stays shippable until parity** (R3): `scripts/build-portable.ps1`, `scripts/package-release.ps1` and the release workflow keep pointing at `BlurLink.Desktop`. No WPF feature work except security fixes.
- **Honesty rules:** exit code 2 means "could not run" and is never a pass; a withdrawn claim is never asserted without its retraction; no protocol constant without evidence; PENDING verdicts are labeled, never faked.
- **Test count baseline:** 246 managed tests pass at plan time (2026-09-14). They must keep passing; tasks add tests and must not delete an existing assertion without saying why.

---

## Rulings (binding where the spec leaves implementation open)

Recorded here so implementers never re-decide them. Each carries what it costs if wrong.

- **R1 — new `BlurLink.Shell`, WPF untouched until M4.** Side-by-side projects keep the WPF app shippable every commit (R3). Cost if wrong: a half-ported tree that ships neither shell; the M4 plan deletes WPF.
- **R2 — `BlurLink.Platform` holds the five UI-free services** (`SingleInstanceGuard`, `HelperLauncher`/`IHelperProcess`/`HelperIpcClient`, `PipeHelperChannel`, `AdapterWatcher`, `BlurProcessWatcher`), moved with `git mv`, namespace `BlurLink.Platform`. Both shells reference it. `ViewModelBase`/`RelayCommand` is copied once into Shell (43 lines, Avalonia-idiomatic) rather than shared, because `ICommand` resolves against different UI assemblies. Cost if wrong: duplicated fixes in two shells for one stage; M4 deletes the WPF copy.
- **R3 — notifications without new packages.** An in-app `NotificationCenter` plus tray tooltip/menu state; no toast library, no balloon API. Cost if wrong: notifications feel quieter than native toasts; accepted for zero-dependency shipping.
- **R4 — first run is a guided checklist over live state, not a wizard engine.** Step states derive from the real Join VMs; no duplicated sniff/start flows. Cost if wrong: none — it cannot drift from the flows it guides.
- **R5 — verify-profile compares user-pasted prefixes.** The sniffer is metadata-only by design, so the tool compares up to N pasted hex samples and records the 12-byte leading run only if ≥3 agree (settles spec Q3 at 12 bytes). Cost if wrong: a mistyped paste writes a bad prefix; mitigated by showing the agreed bytes before writing.
- **R6 — reply-shape validation is length+prefix, observe-only.** Nonce-echo needs query memory the helper does not keep; length+prefix now, nonce-echo later. Counters only, never a drop. Cost if wrong: a shape break outside length/prefix goes unreported; accepted, D11 stays observe-only either way.
- **R7 — tray icon is static art, live text.** One `app.ico`; state reflects in tooltip + menu, not pixels. Cost if wrong: weaker at-a-glance state; accepted, no asset pipeline in this plan.
- **R8 — per-profile memory is one dict.** `HostIpByProfile` (profile name → host overlay IP) only; adapter stays global. Cost if wrong: a second remembered field needs a schema bump later; cheap then.

## Deliberately not in this plan

| Spec item | Where it lives |
|---|---|
| §5 evidence session | the M1 plan, written when the session window opens |
| M4 release (tagged cut, README rewrite, WPF removal, `-Full`/`-Sfx` retirement per D12) | the M4 plan, after M3 parity |
| Payload-rewrite gate work (§5.6, if it opens) | its own design note + workstream, never this plan |
| winget, signing, MSIX, auto-update, cross-platform shipping | non-goals (spec §3) |

## File map

```
src/BlurLink.Shell/BlurLink.Shell.csproj      Task 1 (Avalonia 12.x, net10.0-windows)
src/BlurLink.Shell/Program.cs                 Task 3 (x64 gate, single instance)
src/BlurLink.Shell/App.axaml(.cs)             Task 3 (styles, shutdown, crash hooks in Task 10)
src/BlurLink.Shell/Assets/app.ico             Task 1 (copied, never redrawn per R7)
src/BlurLink.Shell/Themes/Tokens.axaml        Task 3 (design tokens, the only color/type source)
src/BlurLink.Shell/ViewModels/ShellViewModelBase.cs  Task 3 (ViewModelBase + RelayCommand)
src/BlurLink.Shell/ViewModels/MainViewModel.cs       Task 4 (children added per tab task)
src/BlurLink.Shell/Views/MainWindow.axaml(.cs)       Task 4 (nav grows per tab task)
src/BlurLink.Shell/Views/DialogWindow.axaml(.cs)     Task 3 (all message boxes go through it)
src/BlurLink.Shell/Notifications/NotificationCenter.cs Task 3 (in-app notices)
src/BlurLink.Shell/Services/WindowsPlatformServices.cs Task 3 (IPlatformServices impl)
src/BlurLink.Shell/ViewModels/Join*.cs + Views/JoinView.axaml       Task 4
src/BlurLink.Shell/ViewModels/HostViewModel.cs + Views/HostView.axaml Task 5
src/BlurLink.Shell/ViewModels/SettingsViewModel.cs + DiagnosticsViewModel.cs
  + Views/SettingsView.axaml + Views/DiagnosticsView.axaml           Task 6
src/BlurLink.Shell/ViewModels/FirstRunViewModel.cs + Views/FirstRunView.axaml Task 8
src/BlurLink.Shell/ViewModels/VerifyProfileViewModel.cs (+ Diagnostics section) Task 9
src/BlurLink.Platform/*.cs                    Task 2 (moved services + IPlatformServices)
src/BlurLink.Core/FirstRun/BlurFinder.cs + VerifiedProfileWriter.cs Task 8
src/BlurLink.Core/Verify/PrefixStability.cs   Task 9
src/BlurLink.Core/Support/SupportBundle.cs + CrashLog.cs Task 10
src/BlurLink.Core/Net/ReplyShapeValidator.cs  Task 12
tests/BlurLink.Core.Tests/*HeadlessTests.cs   Tasks 3–6, 8–11, 13 ([AvaloniaFact])
```

---

# Stage A — Shell skeleton + parity port

## Task 1: Shell project that builds

**Files:**
- Create: `src/BlurLink.Shell/BlurLink.Shell.csproj`
- Create: `src/BlurLink.Shell/Program.cs`, `src/BlurLink.Shell/App.axaml`, `src/BlurLink.Shell/App.axaml.cs`, `src/BlurLink.Shell/Views/MainWindow.axaml`, `src/BlurLink.Shell/Views/MainWindow.axaml.cs`
- Create: `src/BlurLink.Shell/Assets/app.ico` (binary copy of `src/BlurLink.Desktop/app.ico`)
- Modify: `BlurLink.sln` (add the project)

**Interfaces:**
- Consumes: `Directory.Build.props` (warnings-as-errors, Nullable — inherited, not repeated beyond the Desktop pattern).
- Produces: compilable `BlurLink.Shell` (empty window titled `BlurLink`); every later task builds on it.

- [ ] **Step 1: Pin the Avalonia version**

Run:
```powershell
curl.exe -s --compressed --max-time 25 'https://api.nuget.org/v3/registration5-gz-semver2/avalonia/index.json' > $env:TEMP/av.json
python -c "import json; d=json.load(open(r'$env:TEMP/av.json')); print(d['items'][-1]['lower'],'->',d['items'][-1]['upper'])"
```
Expected: an upper bound like `12.1.2`. Take the newest **stable** 12.x (never an `-rc`). Confirm `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Headless`, `Avalonia.Headless.XUnit` all exist at that version (repeat the index lookup per package; fail the task with BLOCKED if any is missing — do not substitute versions per package). Record the chosen version in the commit message.

- [ ] **Step 2: Write the project file** (with the version from Step 1, shown here as `12.1.2`)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <AssemblyName>BlurLink</AssemblyName>
    <RootNamespace>BlurLink.Shell</RootNamespace>
    <Version>0.2.0</Version>
    <ApplicationIcon>Assets\app.ico</ApplicationIcon>
    <!-- English-only, like the WPF shell. -->
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <Description>BlurLink — Blur LAN discovery bridge. Non-elevated GUI (Avalonia).</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.2" />
    <PackageReference Include="Avalonia.Desktop" Version="12.1.2" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\BlurLink.Contracts\BlurLink.Contracts.csproj" />
    <ProjectReference Include="..\BlurLink.Core\BlurLink.Core.csproj" />
    <ProjectReference Include="..\BlurLink.Platform\BlurLink.Platform.csproj" />
  </ItemGroup>

  <ItemGroup>
    <AvaloniaResource Include="Assets\**" />
  </ItemGroup>

</Project>
```

The `BlurLink.Platform` reference dangles until Task 2 — that is expected; do not build until Task 2 lands the project. (If this offends, Task 2 is the next task for exactly this reason.)

- [ ] **Step 3: Copy the icon and write the skeleton**

```powershell
New-Item -ItemType Directory -Force -Path src/BlurLink.Shell/Assets | Out-Null
Copy-Item src/BlurLink.Desktop/app.ico src/BlurLink.Shell/Assets/app.ico
```

`Program.cs`:
```csharp
using Avalonia;
using System;

namespace BlurLink.Shell;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
```

`App.axaml`:
```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="BlurLink.Shell.App">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

`App.axaml.cs`:
```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BlurLink.Shell.Views;

namespace BlurLink.Shell;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

`Views/MainWindow.axaml`:
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="BlurLink.Shell.Views.MainWindow"
        Title="BlurLink" Width="1080" Height="720" MinWidth="880" MinHeight="600"
        WindowStartupLocation="CenterScreen" Icon="/Assets/app.ico">
  <TextBlock Text="BlurLink" Margin="24" FontSize="15" FontWeight="SemiBold" />
</Window>
```

`Views/MainWindow.axaml.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
```

- [ ] **Step 4: Add to the solution**

Run: `dotnet sln BlurLink.sln add src/BlurLink.Shell/BlurLink.Shell.csproj`
Expected: the sln gains one `BlurLink.Shell` project stanza. Do not touch `scripts/build-portable.ps1` or packaging — the shippable exe stays WPF until M4.

- [ ] **Step 5: Build and run the suite**

Run: `dotnet build BlurLink.sln -c Release --nologo`
Expected: `0 Warning(s) 0 Error(s)` — the WPF project is untouched and the skeleton compiles.

Run: `dotnet test BlurLink.sln -c Release --no-build --nologo`
Expected: `246 passed, 0 failed` — nothing regressed.

- [ ] **Step 6: Commit**

```bash
git add src/BlurLink.Shell BlurLink.sln
git status --short
git commit -m "Add the Avalonia shell skeleton beside WPF (Avalonia 12.1.2, builds green)"
```

`git status --short` must show only `src/BlurLink.Shell/` + `BlurLink.sln`. The commit message records the exact Avalonia version from Step 1.

## Task 2: Platform extraction + the seam

**Files:**
- Create: `src/BlurLink.Platform/BlurLink.Platform.csproj`, `src/BlurLink.Platform/IPlatformServices.cs`
- Move (`git mv`): `src/BlurLink.Desktop/Services/SingleInstanceGuard.cs`, `HelperIpc.cs`, `PipeHelperChannel.cs`, `AdapterWatcher.cs`, `BlurProcessWatcher.cs` → `src/BlurLink.Platform/`
- Modify: `src/BlurLink.Desktop/BlurLink.Desktop.csproj`, `tests/BlurLink.Core.Tests/BlurLink.Core.Tests.csproj` (add the `Platform` ProjectReference), every `using BlurLink.Desktop.Services;` outside the moved files, the moved files' namespace line.

**Interfaces:**
- Consumes: Task 1's Shell project (its dangling `Platform` reference resolves here).
- Produces: `BlurLink.Platform` (`net10.0`, namespace `BlurLink.Platform`) + `IPlatformServices`; both shells reference it.

- [ ] **Step 1: Write the Platform project**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <RootNamespace>BlurLink.Platform</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\BlurLink.Contracts\BlurLink.Contracts.csproj" />
    <ProjectReference Include="..\BlurLink.Core\BlurLink.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Move the five UI-free services**

Run:
```bash
git mv src/BlurLink.Desktop/Services/SingleInstanceGuard.cs src/BlurLink.Platform/SingleInstanceGuard.cs
git mv src/BlurLink.Desktop/Services/HelperIpc.cs src/BlurLink.Platform/HelperIpc.cs
git mv src/BlurLink.Desktop/Services/PipeHelperChannel.cs src/BlurLink.Platform/PipeHelperChannel.cs
git mv src/BlurLink.Desktop/Services/AdapterWatcher.cs src/BlurLink.Platform/AdapterWatcher.cs
git mv src/BlurLink.Desktop/Services/BlurProcessWatcher.cs src/BlurLink.Platform/BlurProcessWatcher.cs
```

In each moved file, replace the namespace line:
```csharp
namespace BlurLink.Desktop.Services;
```
with:
```csharp
namespace BlurLink.Platform;
```
Nothing else changes in those files. They are WPF-free (`System.IO`, `System.IO.Pipes`, `System.Threading`, `System.Diagnostics`, `System.Net.NetworkInformation`, `Contracts`, `Core`) — the build in Step 5 is the proof; if the compiler names a WPF straggler, fix that line and record it in the report.

- [ ] **Step 3: Write the seam**

`src/BlurLink.Platform/IPlatformServices.cs`:
```csharp
namespace BlurLink.Platform;

/// <summary>Shell-implemented OS interactions the view-models need. Implemented
/// once for Windows in BlurLink.Shell; faked in tests. Three methods on purpose —
/// grow this only with a recorded reason, never speculatively.</summary>
public interface IPlatformServices
{
    void CopyToClipboard(string text);
    Task<string?> PickExeFileAsync(string initialPath);
    void OpenFolder(string path);
}
```

- [ ] **Step 4: Rewire the references**

Add to both `src/BlurLink.Desktop/BlurLink.Desktop.csproj` and `tests/BlurLink.Core.Tests/BlurLink.Core.Tests.csproj`:
```xml
<ProjectReference Include="..\..\src\BlurLink.Platform\BlurLink.Platform.csproj" />
```
(Desktop path is `..\BlurLink.Platform\...`; tests path is `..\..\src\BlurLink.Platform\...`.)

Then swap every remaining using. Run:
```powershell
git grep -l 'BlurLink.Desktop.Services' -- src/BlurLink.Desktop tests
```
Expected list: `src/BlurLink.Desktop/App.xaml.cs`, the four Desktop `ViewModels/*.cs`, and the eleven test files (`HelperEmbedTests`, `HelperIpcClientTests`, `HostViewModelTests`, `JoinStoryWiringTests`, `JoinViewModelBusyStateTests`, `JoinViewModelStartFlowTests`, `LogTailTests`, `MainViewModelTests`, `ReplyListenFailureTests`, `SingleInstanceGuardTests`, `TestDoubles`). In each, replace `using BlurLink.Desktop.Services;` with `using BlurLink.Platform;` and nothing else.

Verify: `git grep -rn 'Desktop.Services' -- src tests` prints nothing.

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build BlurLink.sln -c Release --nologo`
Expected: `0 Warning(s) 0 Error(s)` across all five projects.

Run: `dotnet test BlurLink.sln -c Release --no-build --nologo`
Expected: `246 passed, 0 failed` — the move is behavior-preserving, and the suite (which drives these services hard) proves it.

- [ ] **Step 6: Commit**

```bash
git add -A
git status --short
git commit -m "Extract BlurLink.Platform: five UI-free services move, both shells reference it"
```
Staged set must be the five moves + the two csproj edits + the using swaps only. No `bin/`, `obj/`, `out/`, `dist/`.

## Task 3: Tokens, app infrastructure, headless harness

**Files:**
- Create: `src/BlurLink.Shell/Themes/Tokens.axaml`
- Create: `src/BlurLink.Shell/ViewModels/ShellViewModelBase.cs`
- Create: `src/BlurLink.Shell/Notifications/NotificationCenter.cs`
- Create: `src/BlurLink.Shell/Views/DialogWindow.axaml`, `src/BlurLink.Shell/Views/DialogWindow.axaml.cs`
- Create: `src/BlurLink.Shell/Services/WindowsPlatformServices.cs`
- Modify: `src/BlurLink.Shell/Program.cs` (x64 gate + single instance), `src/BlurLink.Shell/App.axaml` (merge Tokens), `src/BlurLink.Shell/App.axaml.cs` (no window yet — stated plainly below)
- Test: `tests/BlurLink.Core.Tests/NotificationCenterTests.cs`, `tests/BlurLink.Core.Tests/DialogHeadlessTests.cs`
- Modify: `tests/BlurLink.Core.Tests/BlurLink.Core.Tests.csproj` (headless packages)

**Interfaces:**
- Consumes: Task 1's skeleton, Task 2's `BlurLink.Platform` + `IPlatformServices`.
- Produces: the design-token source of truth, the VM base, in-app notices, the one dialog every message box uses, the Windows seam impl, and the headless test harness. `ShellViewModelBase` + `RelayCommand` are a verbatim port of the WPF base with only the namespace changed (`System.Windows.Input.ICommand` resolves against Avalonia).

- [ ] **Step 1: Add the headless test packages**

In `tests/BlurLink.Core.Tests/BlurLink.Core.Tests.csproj`, add (same 12.x as Task 1):
```xml
<PackageReference Include="Avalonia.Headless" Version="12.1.2" />
<PackageReference Include="Avalonia.Headless.XUnit" Version="12.1.2" />
```
`Avalonia.Headless.XUnit` provides the `[AvaloniaFact]` attribute, which boots the headless platform per test — use it for every headless test in this plan, never `[Fact]`, or windowing calls throw. Verify at Step 6: if the attribute type does not exist under that exact name in 12.x, use the package's documented xunit entry point instead and record the deviation in the report — do not invent shims.

- [ ] **Step 2: Write the failing tests — notices and dialog**

`tests/BlurLink.Core.Tests/NotificationCenterTests.cs`:
```csharp
using BlurLink.Shell.Notifications;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class NotificationCenterTests
{
    [Fact]
    public void Notify_Adds_VisibleUntilDismissed()
    {
        var center = new NotificationCenter();

        center.Notify("Lobby may appear", "You are reaching the host.", NoticeSeverity.Progress);

        var notice = Assert.Single(center.Notices);
        Assert.Equal("Lobby may appear", notice.Title);
        Assert.Equal(NoticeSeverity.Progress, notice.Severity);

        center.Dismiss(notice);

        Assert.Empty(center.Notices);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var center = new NotificationCenter();
        center.Notify("a", "b", NoticeSeverity.Info);
        center.Notify("c", "d", NoticeSeverity.Error);

        center.Clear();

        Assert.Empty(center.Notices);
    }
}
```

`tests/BlurLink.Core.Tests/DialogHeadlessTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class DialogHeadlessTests
{
    [AvaloniaFact]
    public void Dialog_ShowsTitleAndMessage_ThenCloses()
    {
        var window = new DialogWindow();
        window.Show();
        window.SetMessage("Already running", "Only one window can hold the packet bridge.");

        Assert.Equal("Already running", window.Title);
        Assert.Contains("packet bridge", window.MessageText);

        window.Close();
    }
}
```

- [ ] **Step 3: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "NotificationCenterTests|DialogHeadlessTests" -c Release`
Expected: FAIL — `NotificationCenter`, `DialogWindow` do not exist (`CS0246`).

- [ ] **Step 4: Write the VM base (verbatim port, namespace only)**

`src/BlurLink.Shell/ViewModels/ShellViewModelBase.cs` — copy `src/BlurLink.Desktop/ViewModels/ViewModelBase.cs` exactly, changing only `namespace BlurLink.Desktop.ViewModels;` to `namespace BlurLink.Shell.ViewModels;`. Both `ViewModelBase` (`Set`, `Raise`) and `RelayCommand` (`Execute`, `CanExecute`, `RaiseCanExecuteChanged`) come along unchanged.

- [ ] **Step 5: Implement notices, dialog, and the seam**

`src/BlurLink.Shell/Notifications/NotificationCenter.cs`:
```csharp
using System.Collections.ObjectModel;
using BlurLink.Shell.ViewModels;

namespace BlurLink.Shell.Notifications;

public enum NoticeSeverity { Info, Progress, Warning, Error }

public sealed record Notice(string Title, string Message, NoticeSeverity Severity, DateTime TimestampUtc);

public sealed class NotificationCenter : ShellViewModelBase
{
    public ObservableCollection<Notice> Notices { get; } = new();

    public RelayCommand DismissCommand { get; }

    public NotificationCenter()
    {
        DismissCommand = new RelayCommand(p => { if (p is Notice n) Dismiss(n); });
    }

    public void Notify(string title, string message, NoticeSeverity Severity)
        => Notices.Add(new Notice(title, message, Severity, DateTime.UtcNow));

    public void Dismiss(Notice notice) => Notices.Remove(notice);

    public void Clear() => Notices.Clear();
}
```

`src/BlurLink.Shell/Views/DialogWindow.axaml`:
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="BlurLink.Shell.Views.DialogWindow"
        Title="BlurLink" Width="440" SizeToContent="Height" MinHeight="160"
        WindowStartupLocation="CenterOwner" Icon="/Assets/app.ico"
        CanResize="False">
  <StackPanel Margin="24" Spacing="16">
    <TextBlock x:Name="MessageBlock" TextWrapping="Wrap" FontSize="13" />
    <Button Content="OK" Classes="primary" HorizontalAlignment="Right" MinWidth="96"
            Click="OnOk" AutomationProperties.Name="OK" />
  </StackPanel>
</Window>
```

`src/BlurLink.Shell/Views/DialogWindow.axaml.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell.Views;

public sealed partial class DialogWindow : Window
{
    public DialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public string MessageText => this.FindControl<TextBlock>("MessageBlock")?.Text ?? string.Empty;

    public void SetMessage(string title, string message)
    {
        Title = title;
        var block = this.FindControl<TextBlock>("MessageBlock");
        if (block is not null)
        {
            block.Text = message;
        }
    }

    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var dialog = new DialogWindow();
        dialog.SetMessage(title, message);
        await dialog.ShowDialog(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
```

`src/BlurLink.Shell/Services/WindowsPlatformServices.cs`:
```csharp
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BlurLink.Platform;

namespace BlurLink.Shell.Services;

public sealed class WindowsPlatformServices(Func<TopLevel?> topLevel) : IPlatformServices
{
    public void CopyToClipboard(string text)
    {
        var clipboard = topLevel()?.Clipboard;
        if (clipboard is not null)
        {
            _ = clipboard.SetTextAsync(text);
        }
    }

    public async Task<string?> PickExeFileAsync(string initialPath)
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var exeType = new FilePickerFileType("Executables")
        {
            Patterns = new[] { "*.exe" },
        };
        var suggested = string.IsNullOrWhiteSpace(initialPath) ? null : await storage.TryGetFileFromPathAsync(initialPath);
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate Blur.exe",
            AllowMultiple = false,
            FileTypeFilter = new[] { exeType },
            SuggestedFile = suggested,
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
```

- [ ] **Step 6: Harden Program.cs and restyle the App**

`Program.cs` becomes (single-instance guard + x64 gate; the focus fallback mirrors the WPF one using process APIs only):
```csharp
using Avalonia;
using System;
using System.Diagnostics;
using BlurLink.Core.Logging;
using BlurLink.Platform;

namespace BlurLink.Shell;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
        {
            AppLog.Error("BlurLink supports Windows 10/11 x64 only.");
            return 2;
        }

        using var singleInstance = SingleInstanceGuard.Acquire();
        if (!singleInstance.IsAcquired)
        {
            FocusExistingInstance();
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void FocusExistingInstance()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id == self.Id || other.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    _ = ShowWindow(other.MainWindowHandle, 9);
                    _ = SetForegroundWindow(other.MainWindowHandle);
                    return;
                }
            }
        }
        catch
        {
            // cosmetic only — the running window stays where it is
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr nWnd, int nCmdShow);
}
```

`App.axaml` gains the token dictionary (Task 3 writes `Themes/Tokens.axaml` below first):
```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="BlurLink.Shell.App">
  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://BlurLink.Shell/Themes/Tokens.axaml" />
  </Application.Styles>
</Application>
```

`Themes/Tokens.axaml` — the only color/type source. Values ported 1:1 from `src/BlurLink.Desktop/Themes/Theme.xaml` (same hex, same font stacks). Views must reference these keys and never hardcode a color:
```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Color x:Key="BgColor">#0B0D12</Color>
  <Color x:Key="SidebarColor">#0F1218</Color>
  <Color x:Key="CardColor">#141822</Color>
  <Color x:Key="CardHoverColor">#191E2A</Color>
  <Color x:Key="BorderColor">#232936</Color>
  <Color x:Key="InputColor">#0F1219</Color>
  <Color x:Key="AccentColor">#7DD3FC</Color>
  <Color x:Key="AccentDimColor">#14324A</Color>
  <Color x:Key="TextColor">#E9ECF4</Color>
  <Color x:Key="MutedColor">#8B93A7</Color>
  <Color x:Key="FaintColor">#6B7386</Color>
  <Color x:Key="SuccessColor">#34D399</Color>
  <Color x:Key="WarningColor">#FBBF24</Color>
  <Color x:Key="DangerColor">#F87171</Color>
  <SolidColorBrush x:Key="Bg" Color="{StaticResource BgColor}" />
  <SolidColorBrush x:Key="Sidebar" Color="{StaticResource SidebarColor}" />
  <SolidColorBrush x:Key="Card" Color="{StaticResource CardColor}" />
  <SolidColorBrush x:Key="Border" Color="{StaticResource BorderColor}" />
  <SolidColorBrush x:Key="Accent" Color="{StaticResource AccentColor}" />
  <SolidColorBrush x:Key="AccentDim" Color="{StaticResource AccentDimColor}" />
  <SolidColorBrush x:Key="Text" Color="{StaticResource TextColor}" />
  <SolidColorBrush x:Key="Muted" Color="{StaticResource MutedColor}" />
  <SolidColorBrush x:Key="Faint" Color="{StaticResource FaintColor}" />
  <SolidColorBrush x:Key="Success" Color="{StaticResource SuccessColor}" />
  <SolidColorBrush x:Key="Warning" Color="{StaticResource WarningColor}" />
  <SolidColorBrush x:Key="Danger" Color="{StaticResource DangerColor}" />
  <FontFamily x:Key="AppFont">Segoe UI Variable Text, Segoe UI</FontFamily>
  <FontFamily x:Key="DisplayFont">Segoe UI Variable Display, Segoe UI</FontFamily>
  <FontFamily x:Key="MonoFont">Cascadia Code, Consolas</FontFamily>
  <x:Double x:Key="FontSizeSmall">11.5</x:Double>
  <x:Double x:Key="CornerRadius">9</x:Double>
  <x:Double x:Key="Space1">4</x:Double>
  <x:Double x:Key="Space2">8</x:Double>
  <x:Double x:Key="Space3">12</x:Double>
  <x:Double x:Key="Space4">16</x:Double>
  <x:Double x:Key="Space5">24</x:Double>
</ResourceDictionary>
```

And the four shared control styles (same file, appended):
```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style Selector="Button.primary">
    <Setter Property="Background" Value="{StaticResource AccentDim}" />
    <Setter Property="Foreground" Value="{StaticResource Accent}" />
    <Setter Property="CornerRadius" Value="{StaticResource CornerRadius}" />
    <Setter Property="Padding" Value="16,8" />
    <Setter Property="FontWeight" Value="SemiBold" />
  </Style>
  <Style Selector="Button.primary:pointerover">
    <Setter Property="Background" Value="{StaticResource Accent}" />
    <Setter Property="Foreground" Value="{StaticResource Bg}" />
  </Style>
  <Style Selector="Button.primary:disabled">
    <Setter Property="Opacity" Value="0.45" />
  </Style>
  <Style Selector="Button.quiet">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="{StaticResource Muted}" />
    <Setter Property="Padding" Value="12,5" />
  </Style>
  <Style Selector="Button.quiet:pointerover">
    <Setter Property="Foreground" Value="{StaticResource Text}" />
  </Style>
  <Style Selector="Button.nav">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="{StaticResource Muted}" />
    <Setter Property="CornerRadius" Value="999" />
    <Setter Property="Padding" Value="14,7" />
    <Setter Property="FontSize" Value="13.5" />
  </Style>
  <Style Selector="Button.nav.active">
    <Setter Property="Background" Value="{StaticResource AccentDim}" />
    <Setter Property="Foreground" Value="{StaticResource Accent}" />
  </Style>
  <Style Selector="Border.card">
    <Setter Property="Background" Value="{StaticResource Card}" />
    <Setter Property="BorderBrush" Value="{StaticResource Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="12" />
    <Setter Property="Padding" Value="20" />
  </Style>
  <Style Selector="TextBlock.h1">
    <Setter Property="FontSize" Value="20" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="FontFamily" Value="{StaticResource DisplayFont}" />
  </Style>
  <Style Selector="TextBlock.h2">
    <Setter Property="FontSize" Value="15" />
    <Setter Property="FontWeight" Value="SemiBold" />
  </Style>
  <Style Selector="TextBlock.label">
    <Setter Property="FontSize" Value="12" />
    <Setter Property="Foreground" Value="{StaticResource Muted}" />
  </Style>
  <Style Selector="TextBlock.faint">
    <Setter Property="Foreground" Value="{StaticResource Faint}" />
  </Style>
  <Style Selector="TextBlock.mono">
    <Setter Property="FontFamily" Value="{StaticResource MonoFont}" />
  </Style>
</Styles>
```

Note: a `.axaml` file holds one root. Put the `ResourceDictionary` part in `Themes/Tokens.axaml` and the `Styles` part in `Themes/Controls.axaml`, and include both in `App.axaml` (two `StyleInclude` lines, Tokens first). The two code blocks above are the two files.

Stated plainly: after this task the app compiles and its tests pass, but it shows no window — Task 4 wires `MainWindow`. Verified by build, not by launch; do not claim a launch.

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "NotificationCenterTests|DialogHeadlessTests" -c Release`
Expected: PASS (2 + 1 tests).

Run the full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `249 passed, 0 failed` (246 + 3).

- [ ] **Step 8: Commit**

```bash
git add src/BlurLink.Shell tests/BlurLink.Core.Tests/NotificationCenterTests.cs tests/BlurLink.Core.Tests/DialogHeadlessTests.cs tests/BlurLink.Core.Tests/BlurLink.Core.Tests.csproj
git commit -m "Shell infrastructure: tokens, VM base, notices, dialog, platform seam, headless harness"
```

## Task 4: Join tab — MainWindow, MainViewModel, and the monolith split

**Files:**
- Create: `src/BlurLink.Shell/ViewModels/JoinSessionViewModel.cs`, `BridgeSettingsViewModel.cs`, `DiscoverySniffViewModel.cs`, `JoinViewModel.cs` (thin facade), `MainViewModel.cs`
- Create: `src/BlurLink.Shell/Views/JoinView.axaml`, `JoinView.axaml.cs`
- Modify: `src/BlurLink.Shell/Views/MainWindow.axaml`, `MainWindow.axaml.cs`, `src/BlurLink.Shell/App.axaml.cs` (create the composition root)
- Test: `tests/BlurLink.Core.Tests/JoinHeadlessTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3 (project, `Platform` services, tokens, base, dialog, `IPlatformServices`); `SessionCoordinator`/`SessionStoryTable`/`SessionState` (unchanged); the WPF `JoinViewModel.cs` + `Views/JoinView.xaml` as the port source (same sections, same order, same copy).
- Produces: a launchable app with the Join destination; `JoinViewModel` facade with `ForTests`/`ReadyForTests`/`ApplyStatusForTests` seams; `MainViewModel` with `CurrentView`, `StatusBar`, `CopyDiagnosticsCommand`, adapter fan-out. Tasks 5–6 add the Host/Settings/Diagnostics children and nav buttons.

- [ ] **Step 1: Write the failing headless tests**

`tests/BlurLink.Core.Tests/JoinHeadlessTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Core.Session;
using BlurLink.Platform;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class JoinHeadlessTests
{
    private static JoinViewModel ForBridge() => JoinViewModel.ForTests(new ScriptedChannel());

    [AvaloniaFact]
    public void ForwardingStory_RendersTheHeadline()
    {
        var vm = ForBridge();
        vm.ApplyStatusForTests(new IpcStatusResponse { Active = true, Captured = 4, Forwarded = 4 });
        var view = new JoinView { DataContext = vm };
        view.Show();

        try
        {
            var headline = view.FindControl<Avalonia.Controls.TextBlock>("StoryHeadline");
            Assert.NotNull(headline);
            Assert.Equal("You are reaching the host", headline.Text);
            Assert.Equal("bridge-forwarding", vm.Story.Code);
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void Start_IsDisabled_UntilPreflightPasses()
    {
        var vm = ForBridge();
        var view = new JoinView { DataContext = vm };
        view.Show();

        try
        {
            var start = view.FindControl<Avalonia.Controls.Button>("StartStopButton");
            Assert.NotNull(start);
            Assert.False(vm.PreflightReady);
            Assert.False(start.IsEnabled);
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void ApplyingADetectedPort_FillsTheSettingsField()
    {
        var vm = ForBridge();
        vm.Sniff.SniffCandidate = 50001;

        vm.Sniff.ApplySniffCommand.Execute(null);

        Assert.Equal("50001", vm.Settings.DiscoveryPort);
    }

    [AvaloniaFact]
    public void DismissCommand_RemovesTheNotice()
    {
        var vm = ForBridge();
        var center = new BlurLink.Shell.Notifications.NotificationCenter();
        center.Notify("t", "m", BlurLink.Shell.Notifications.NoticeSeverity.Info);
        var notice = Assert.Single(center.Notices);

        center.DismissCommand.Execute(notice);

        Assert.Empty(center.Notices);
    }
}
```

`ScriptedChannel` already exists in the test assembly (`SessionCoordinatorTests.cs`, internal) — reuse it, do not redefine it.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter JoinHeadlessTests -c Release`
Expected: FAIL — `JoinViewModel`, `JoinView` do not exist.

- [ ] **Step 3: Implement the three parts + facade**

Port the WPF `JoinViewModel.cs` (1432 lines) by moving each member into exactly one new file. Boundaries:

`BridgeSettingsViewModel` — fields and validation only, no helper IO except adapter enumeration: `Adapters`, `SelectedAdapter`, `HostIp`, `DiscoveryPort`, `BroadcastDestination`, `PayloadHex`, `PreserveBroadcast`, `RefreshAdaptersCommand`, `RefreshAdapters()`, `RefreshFromConfig()`, `TryRoute()`, `RouteWarning`, `RouteDetails`, `PreflightItems`, `PreflightChecklist`, `PreflightSummary`, `PreflightReady`, `ComputePreflight()`, `ValidateInputs(out int port, out string error)` (same rule as WPF: IPv4 host, port 1–65535, broadcast valid, signature parses). Constructor: `(BlurLinkConfig config, Action onChanged, Action capabilitiesChanged)`. It raises `capabilitiesChanged` wherever WPF called `RaisePreflight()` for capability reasons, so the session part can re-sync the coordinator.

`DiscoverySniffViewModel` — `SniffRunning`, `SniffStatus`, `RepliesSummary`, `FormatReplySummary` (static, verbatim port), `SniffCandidate`, `ShowApplySniff`, `DetectCommand`, `ListenRepliesCommand`, `StopSniffCommand`, `ApplySniffCommand`, `SniffAsync(bool replies)`, `SendSniffRequestAsync`, `StopSniffAsync`, `ApplySniff()`, `UpdateSniffFromStatus`, `FinishReplyListen`. Constructor takes `(IHelperProcess launcher, Func<...> connect, Action dropConnection, Action<string> status, BridgeSettingsViewModel settings, Func<bool> isSessionRunning, Action<string> applyPort)`. `ApplySniff()` sets the port through `applyPort` (wired to the settings VM), never touching settings directly.

`JoinSessionViewModel` — coordinator, story, session, game: `Story`, `RawCounters`, `Counters`, `BridgeRunning`, `HelperLost`, `ActiveFilter`, `BlurStatus`, `Message`, `RecentEvents`, `StartStopCommand`, `ForceKillCommand`, `LaunchBlurCommand`, `PollStatusCommand`, `ToggleAsync`, `StartAsync`, `StopAsync`, `WaitForHelperExitAsync`, `ForceKill`, `StartPolling`, `PollLoopAsync`, `PollOnceAsync`, `LaunchBlur`, `OnBlurExited`, `AttachBlur`, `ForTests`, `ReadyForTests`, `ApplyStatusForTests`, `UpdateCoordinatorCapabilities`, `OnCoordinatorStateChanged`, `RefreshStoryAsync`, `IsHelperError`, `Dispose` (poll CTS, rescan timer, blur watcher), plus `public event Action<SessionState>? StateChanged` forwarding the coordinator's event (MainViewModel and Task 11 subscribe to it — do not skip this member). Constructor takes the same six WPF arguments plus the two sibling VMs. The `StartStopCommand` can-execute additionally requires `!Sniff.IsRunning` — subscribe to the sniffer's `PropertyChanged` for `SniffRunning` and raise.

`JoinViewModel` facade — owns the three parts, forwards `RefreshFromConfig()` and `Dispose()`, exposes `Session`, `Settings`, `Sniff` properties for binding (`{Binding Session.Story.Headline}` style), plus `ForTests`/`ReadyForTests`/`ApplyStatusForTests` delegating to the session part and `Story`/`PreflightReady` passthroughs the tests use. Under 120 lines; any logic beyond forwarding is a defect.

- [ ] **Step 4: Implement MainViewModel + MainWindow with the Join destination**

`MainViewModel` ports the WPF one with three structural changes: constructor `(BlurLinkConfig config, NotificationCenter notices, IPlatformServices platform)` (App.axaml.cs composes it — no parameterless ctor, so tests always state their doubles), a `public NotificationCenter Notices { get; }` the banner binds, and `StatusChipText` + `StatusChipKind` (`Idle`/`Running`/`Lost` enum on the VM) updated from the session child's forwarded `StateChanged` event. `CurrentView`/`StatusBar`/`NavigateCommand`/`CopyDiagnosticsCommand` (identical diagnostics text plus a `[Shell]` line noting the Avalonia build), adapter fan-out, and `RefreshAllAdapters` port 1:1; `CopyDiagnostics` copies through `IPlatformServices`. `CurrentPane` returns the child matching `CurrentView` (Join in this task; Host/Settings/Diagnostics added by their tasks).

`MainWindow.axaml` — header brand + nav (one button: Join) + status chip bound to `Join.Session.BridgeRunning` / `Join.Session.HelperLost` + content host + notification banner + footer. Exact structure (bindings must match VM names above):
```xml
<Window xmlns="https://github.com/avaloniaui" ... x:Class="BlurLink.Shell.Views.MainWindow"
        Title="BlurLink" Width="1080" Height="720" MinWidth="880" MinHeight="600"
        WindowStartupLocation="CenterScreen" Icon="/Assets/app.ico">
  <Grid RowDefinitions="Auto,*,Auto">
    <Border Grid.Row="0" Padding="20,14" Background="{StaticResource Sidebar}">
      <DockPanel LastChildFill="True">
        <StackPanel DockPanel.Dock="Left" Orientation="Horizontal" VerticalAlignment="Center">
          <Border Width="30" Height="30" CornerRadius="9" Background="{StaticResource AccentDim}">
            <TextBlock Text="B" FontSize="16" FontWeight="Bold" Foreground="{StaticResource Accent}"
                       HorizontalAlignment="Center" VerticalAlignment="Center" />
          </Border>
          <StackPanel Margin="11,0,0,0" VerticalAlignment="Center">
            <TextBlock Text="BlurLink" FontSize="15" FontWeight="SemiBold" />
            <TextBlock Text="LAN discovery bridge" FontSize="10.5" Classes="faint" />
          </StackPanel>
        </StackPanel>
        <StackPanel DockPanel.Dock="Left" Orientation="Horizontal" HorizontalAlignment="Center" Margin="20,0,0,0">
          <Button Content="Join" Classes="nav active" Command="{Binding NavigateCommand}" CommandParameter="Join"
                  AutomationProperties.Name="Join tab" />
        </StackPanel>
        <Border DockPanel.Dock="Right" CornerRadius="999" Padding="13,6" HorizontalAlignment="Right" VerticalAlignment="Center">
          <StackPanel Orientation="Horizontal">
            <Ellipse x:Name="StatusDot" Width="8" Height="8" Margin="0,0,8,0" VerticalAlignment="Center" Fill="{StaticResource Muted}" />
            <TextBlock Text="{Binding StatusChipText}" FontWeight="SemiBold" FontSize="12" VerticalAlignment="Center" />
          </StackPanel>
        </Border>
      </DockPanel>
    </Border>
    <ScrollViewer Grid.Row="1" Margin="28,22,28,10">
      <StackPanel Spacing="12">
        <ItemsControl ItemsSource="{Binding Notices.Notices}">
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <Border Classes="card" Padding="12">
                <DockPanel LastChildFill="True">
                  <Button DockPanel.Dock="Right" Content="Dismiss" Classes="quiet"
                          Command="{Binding $parent[ItemsControl].DataContext.Notices.DismissCommand}" ... />
```
No — `Dismiss` is a method, not a command. Give `NotificationCenter` a `RelayCommand DismissCommand` taking the notice as parameter (add it in this task: `public RelayCommand DismissCommand { get; }` constructed with `new RelayCommand(p => { if (p is Notice n) Dismiss(n); })`). Update `NotificationCenterTests` expectations? It tests methods — keep methods, add the command; no test change needed. Continue the window:
```xml
                  <StackPanel>
                    <TextBlock Text="{Binding Title}" FontWeight="SemiBold" />
                    <TextBlock Text="{Binding Message}" TextWrapping="Wrap" Classes="faint" />
                  </StackPanel>
                </DockPanel>
              </Border>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <ContentControl Content="{Binding CurrentPane}" />
      </StackPanel>
    </ScrollViewer>
    <Border Grid.Row="2" Background="{StaticResource Sidebar}" Padding="20,8">
      <DockPanel LastChildFill="False">
        <TextBlock DockPanel.Dock="Left" Text="{Binding StatusBar}" Classes="faint" FontSize="11.5" VerticalAlignment="Center" />
        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
          <TextBlock Text="{Binding Join.Session.RawCounters}" Classes="mono faint" FontSize="10.5" VerticalAlignment="Center" />
          <Button Content="Copy diagnostics" Classes="quiet" Command="{Binding CopyDiagnosticsCommand}" Margin="14,0,0,0"
                  AutomationProperties.Name="Copy diagnostics" />
        </StackPanel>
      </DockPanel>
    </Border>
  </Grid>
</Window>
```

`MainViewModel` exposes `CurrentPane` (object? — the `JoinViewModel` facade; the view side resolves via a `DataTemplate` in `App.axaml`: `<DataTemplate DataType="{x:Type vm:JoinViewModel}"><views:JoinView /></DataTemplate>` — DataTemplates live in `App.axaml`, one per tab, added by each tab task). `StatusChipText` derives from session state ("Bridge running" / "Helper lost" / "Bridge stopped" — same three strings as WPF). The status dot fill needs a converter — add `BoolToBrush`? Keep code-behind minimal: bind dot `Fill` via a `StatusChipBrush` property (SolidColorBrush built from token colors by key name? View-models must not know Avalonia types... `IBrush` is Avalonia. Alternative: `StatusChipKind` enum { Idle, Running, Lost } + three `DataTrigger`-style selectors — Avalonia Styles can't trigger on ancestor DataContext property without binding... they can: `<Style Selector="Ellipse.dot-running">` + code-behind setting Classes on state change. Simplest honest: MainWindow code-behind subscribes to shell `PropertyChanged` for `StatusChipKind` and sets `StatusDot.Classes` / `Fill` from `FindResource`. Specify exactly that (12 lines, no logic).

Deterministic close (port of WPF `Closing` handler): on `Closing`, if `Join.Session.BridgeRunning`, cancel, `await Join.Session.StopAsync()`, then `Close()`. Exact port.

`App.axaml.cs` now builds the composition root: config store load, `WindowsPlatformServices(() => MainWindow)`, `MainViewModel`, `MainWindow { DataContext = vm }`, dark title bar? (WPF DWM call — port via Win32 interop in code-behind with same try/catch; specify exact same P/Invoke, guarded by `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)`).

`JoinView.axaml` — port every section of `src/BlurLink.Desktop/Views/JoinView.xaml` in order (pre-flight card, bridge card with `x:Name="StartStopButton"`, story banner with `x:Name="StoryHeadline"`, counters disclosure, sniff/advanced section, Blur launch row, recent events). Binding parity rule: every `{Binding X}` or `{Binding Session.X}` used must exist on the facade/parts; verification step runs a grep-compare:
```powershell
$wpf = Select-String -Pattern '\{Binding ([\w.]+)' -Path src/BlurLink.Desktop/Views/JoinView.xaml | ForEach-Object { $_.Matches.Groups[1].Value } | Sort-Object -Unique
$ava = Select-String -Pattern '\{Binding ([\w.]+)' -Path src/BlurLink.Shell/Views/JoinView.axaml | ForEach-Object { $_.Matches.Groups[1].Value } | Sort-Object -Unique
Compare-Object $wpf $ava
```
Expected: only intended renames (`Session.`/`Settings.`/`Sniff.` prefixes); every leaf name resolves. Name the story headline `StoryHeadline` and the start button `StartStopButton` (tests depend on them).

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter JoinHeadlessTests -c Release`
Expected: PASS (4 tests).

Full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `253 passed, 0 failed` (249 + 4).

Manual sanity: launch `src/BlurLink.Shell/bin/Release/net10.0-windows/BlurLink.exe` unelevated; Join shows the helper-not-running story; Start is disabled until preflight passes. Record what was seen; no screenshot claims.

- [ ] **Step 6: Commit**

```bash
git add src/BlurLink.Shell tests/BlurLink.Core.Tests/JoinHeadlessTests.cs
git commit -m "Port the Join tab to the Shell: MainWindow, MainViewModel, monolith split in three"
```

## Task 5: Host tab port

**Files:**
- Create: `src/BlurLink.Shell/ViewModels/HostViewModel.cs`, `src/BlurLink.Shell/Views/HostView.axaml`, `HostView.axaml.cs`
- Modify: `src/BlurLink.Shell/ViewModels/MainViewModel.cs` (+ `Host` child, fan-out), `src/BlurLink.Shell/Views/MainWindow.axaml` (+ Host nav button), `src/BlurLink.Shell/App.axaml` (+ Host DataTemplate)
- Test: `tests/BlurLink.Core.Tests/HostHeadlessTests.cs`

**Interfaces:**
- Consumes: Task 4 (shell, MainWindow pattern, `ForTests` seams); WPF `ViewModels/HostViewModel.cs` + `Views/HostView.xaml` as the port source.
- Produces: Host destination at parity; Task 6 adds the last two destinations.

The old `HostViewModel.FormatStatus` sentence builder is deliberately retired, not ported: the status sentence comes only from `SessionStoryTable` via the VM's own coordinator (spec §7.2), and refusal detail moves to the Diagnostics panel in Task 12. The WPF VM and its tests stay untouched until M4.

- [ ] **Step 1: Write the failing headless tests**

`tests/BlurLink.Core.Tests/HostHeadlessTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Core.Session;
using BlurLink.Platform;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class HostHeadlessTests
{
    [AvaloniaFact]
    public void WaitingStory_RendersWithoutClaimingFailure()
    {
        var vm = HostViewModel.ForTests(new ScriptedChannel());
        vm.ApplyStatusForTests(new IpcStatusResponse { HostActive = true });
        var view = new HostView { DataContext = vm };
        view.Show();

        try
        {
            Assert.Equal("host-waiting", vm.Story.Code);
            var headline = view.FindControl<Avalonia.Controls.TextBlock>("HostStoryHeadline");
            Assert.NotNull(headline);
            Assert.Equal("Waiting for a player", headline.Text);
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void PlayerRow_RendersOverlayAndLan()
    {
        var vm = HostViewModel.ForTests(new ScriptedChannel());
        vm.ApplyStatusForTests(new IpcStatusResponse
        {
            HostActive = true,
            HostPlayers =
            {
                new HostPlayerStatus { OverlayIp = "10.0.0.200", LanIp = "192.168.0.200", BlurSourcePort = 50001, InFilter = true },
            },
        });
        var view = new HostView { DataContext = vm };
        view.Show();

        try
        {
            var list = view.FindControl<Avalonia.Controls.ItemsControl>("PlayerList");
            Assert.NotNull(list);
            Assert.Single(vm.Players);
            Assert.Equal("10.0.0.200", vm.Players[0].OverlayIp);
        }
        finally
        {
            view.Close();
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter HostHeadlessTests -c Release`
Expected: FAIL — `HostViewModel`, `HostView` do not exist.

- [ ] **Step 3: Implement the Host VM (mechanics ported, sentence from the story table)**

Port `HostViewModel.cs` 1:1 except: add a `SessionCoordinator _coordinator` (same construction pattern as the Join session part, over `PipeHelperChannel` in production and `ForTests(IHelperChannel)` in tests, with `ReadyForTests()` all-ready), add `public SessionStory Story => SessionStoryTable.Describe(_coordinator.State);`, add `public event Action<SessionState>? StateChanged` forwarding the coordinator's event (MainViewModel and Task 11 subscribe — do not skip this member), push every `ApplyStatus` through `_coordinator.ApplyStatus` (and error envelopes through `ApplyError`, mirroring the Join part's `IsHelperError` guard), and delete `FormatStatus` (no replacement — the story banner is the sentence). Keep verbatim: `HostPlayerRow` (all seven members), `Adapters`/`SelectedAdapter`, `DiscoveryPort`, `AutoAccept`, `HostRunning`/`ShowStartHost`, `Counters` (`heard=`/`forwarded=`), `IsBusy`, `ForwardsHeard`, `RepliesForwarded`, `ShowNoPlayers`, `ActiveFilter`, `StatusText` (kept as the last-action line — "Revoked x.", "Host mode stopped." — not the session sentence; the story banner carries that), all four commands, `StartHostAsync`/`StopHostAsync`/`RevokeAsync`/`ApplyResponse`/`ApplyStatus`/`RefreshFromConfig`/`RefreshAdapters`/`BuildPreflight`/`RaisePreflight`/`ParsedPort`/`Dispose`.

- [ ] **Step 4: Port the Host view + wire nav**

`HostView.axaml` ports `HostView.xaml` in order with these bindings (every one must resolve): `Adapters`, `FriendlyName`, `AutoAccept`, `Counters`, `Players`, `Summary`, `Preflight`, `RefreshAdaptersCommand`, `StartHostCommand`, `StopHostCommand`, `StatusText`, plus the story banner (`x:Name="HostStoryHeadline"` bound to `Story.Headline`, detail + action lines) and the player list (`x:Name="PlayerList"` ItemsControl with a Revoke button per row bound to `RevokePlayerCommand` with the row as parameter — Avalonia: `Command="{Binding $parent[UserControl].DataContext.RevokePlayerCommand}" CommandParameter="{Binding}"`).

`App.axaml` gains:
```xml
<DataTemplate DataType="{x:Type hvm:HostViewModel}">
  <hviews:HostView />
</DataTemplate>
```
(with `xmlns:hvm="clr-namespace:BlurLink.Shell.ViewModels;assembly=BlurLink.Shell"` and the views namespace; same pattern Task 4 used for Join).

`MainViewModel` gains the `Host` child (same six ctor arguments as Join), `RefreshAllAdapters` fans out to it, and `MainWindow.axaml` gains the Host nav button (`CommandParameter="Host"`, `AutomationProperties.Name="Host tab"`). `CurrentPane` returns the child matching `CurrentView`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter HostHeadlessTests -c Release`
Expected: PASS (2 tests).

Full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `255 passed, 0 failed` (253 + 2).

Manual sanity: launch unelevated, open Host: prerequisite line reads ready-or-what-is-missing, player list empty without claiming failure.

- [ ] **Step 6: Commit**

```bash
git add src/BlurLink.Shell tests/BlurLink.Core.Tests/HostHeadlessTests.cs
git commit -m "Port the Host tab to the Shell with its story from the session model"
```

## Task 6: Settings + Diagnostics port (parity complete)

**Files:**
- Create: `src/BlurLink.Shell/ViewModels/SettingsViewModel.cs`, `DiagnosticsViewModel.cs`, `src/BlurLink.Shell/Views/SettingsView.axaml(.cs)`, `DiagnosticsView.axaml(.cs)`
- Modify: `src/BlurLink.Shell/ViewModels/MainViewModel.cs` (+ two children, fan-out), `Views/MainWindow.axaml` (+ two nav buttons), `App.axaml` (+ two DataTemplates)
- Test: `tests/BlurLink.Core.Tests/SettingsHeadlessTests.cs`, `DiagnosticsHeadlessTests.cs`

**Interfaces:**
- Consumes: Tasks 4–5 (patterns); WPF `SettingsViewModel.cs` as the port source; `IPlatformServices` (no `System.Windows` calls survive — Clipboard, OpenFileDialog, Process.Start all go through the seam).
- Produces: all four destinations live; parity checklist closable (single instance, attach, stop semantics, import/export, log viewer).

The helper-log viewer moves to Diagnostics here (spec §7.3: the log viewer moves under Diagnostics). Settings keeps game path/args, log level, rate limit, route test, ping, adapters summary, export/import, open-log-folder.

- [ ] **Step 1: Write the failing headless tests**

`tests/BlurLink.Core.Tests/SettingsHeadlessTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class SettingsHeadlessTests
{
    [AvaloniaFact]
    public void Export_ThenImport_RoundTripsTheHostIp()
    {
        var services = new FakePlatformServices();
        var config = BlurLinkConfig.CreateDefault();
        var saved = 0;
        var vm = new SettingsViewModel(config, () => saved++, _ => { }, services);
        config.HostOverlayIp = "10.0.0.10";

        vm.ExportCommand.Execute(null);
        var text = vm.ExportText;
        Assert.Contains("10.0.0.10", text);

        config.HostOverlayIp = string.Empty;
        vm.ImportCommand.Execute(null);

        Assert.Equal("10.0.0.10", config.HostOverlayIp);
        Assert.Equal("Configuration imported.", vm.Message);
    }

    [AvaloniaFact]
    public void UnknownLogLevel_NormalizesOnSave()
    {
        var services = new FakePlatformServices();
        string? applied = null;
        var config = BlurLinkConfig.CreateDefault();
        var vm = new SettingsViewModel(config, () => { }, level => applied = level, services);
        vm.LogLevel = "Verbose";

        vm.SaveCommand.Execute(null);

        Assert.Equal(BlurLinkConstants.DefaultLogLevel, config.LogLevel);
        Assert.Equal(BlurLinkConstants.DefaultLogLevel, applied);
    }

    [AvaloniaFact]
    public void SettingsView_Renders()
    {
        var services = new FakePlatformServices();
        var vm = new SettingsViewModel(BlurLinkConfig.CreateDefault(), () => { }, _ => { }, services);
        var view = new SettingsView { DataContext = vm };
        view.Show();

        try
        {
            Assert.NotNull(view.FindControl<Avalonia.Controls.Button>("SaveButton"));
        }
        finally
        {
            view.Close();
        }
    }
}
```

`tests/BlurLink.Core.Tests/DiagnosticsHeadlessTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class DiagnosticsHeadlessTests
{
    [AvaloniaFact]
    public void EmptyLogDir_ExplainsItself()
    {
        var vm = new DiagnosticsViewModel(System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString()));
        var view = new DiagnosticsView { DataContext = vm };
        view.Show();

        try
        {
            Assert.Contains("No helper log yet", vm.HelperLogStatus);
            Assert.NotNull(view.FindControl<Avalonia.Controls.Button>("CopyLogButton"));
        }
        finally
        {
            view.Close();
        }
    }
}
```

`FakePlatformServices` (test-only, in `SettingsHeadlessTests.cs` — later tasks reuse it from this file, do not redefine):
```csharp
internal sealed class FakePlatformServices : BlurLink.Platform.IPlatformServices
{
    public List<string> Copied { get; } = new();
    public string? PickResult { get; set; }
    public List<string> OpenedFolders { get; } = new();

    public void CopyToClipboard(string text) => Copied.Add(text);
    public Task<string?> PickExeFileAsync(string initialPath) => Task.FromResult(PickResult);
    public void OpenFolder(string path) => OpenedFolders.Add(path);
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "SettingsHeadlessTests|DiagnosticsHeadlessTests" -c Release`
Expected: FAIL — the Shell VMs and views do not exist.

- [ ] **Step 3: Implement the Settings VM (seam-swapped port)**

Port `SettingsViewModel.cs` 1:1 with exactly three behavior changes, all seam swaps: `System.Windows.Clipboard.SetText` → `_platform.CopyToClipboard`; `new Microsoft.Win32.OpenFileDialog` → `await _platform.PickExeFileAsync(BlurPath)` (Browse becomes async: `BrowseCommand = new RelayCommand(_ => _ = BrowseAsync())`); `Process.Start(folder)` → `_platform.OpenFolder(dir)`. Constructor gains a final `IPlatformServices platform` parameter: `(BlurLinkConfig config, Action onChanged, Action<string>? applyLogLevel = null, IPlatformServices? platform = null)` defaulting to a throwaway that throws `InvalidOperationException` on use (production always passes the real one; tests pass the fake). Everything else stays: `BlurPath`, `BlurArgs`, `LogLevel`/`LogLevels`/`ApplyLogLevel`, `RateLimit`/`MaxRateLimit`, `Message`, `LogPath`, `RouteResult`, `PingResult`, `ExportText`, `AdaptersSummary`, `ResearchSteps`, all twelve commands, `RefreshHelperLog`/`CopyHelperLog`/`HelperLogLines`/`HelperLogStatus`/`AutoRefreshHelperLog`/`IsSettingsTabVisible`/timer logic — EXCEPT the helper-log members move to `DiagnosticsViewModel` (next step), deleted here. `Save`/`Reset`/`Import` keep the apply-level + refresh + `onChanged` order.

- [ ] **Step 4: Implement the Diagnostics VM (log viewer, moved)**

`DiagnosticsViewModel(ShellViewModelBase)` owns what left Settings: `HelperLogLines`, `HelperLogStatus`, `AutoRefreshHelperLog`, `IsVisible` (replaces `IsSettingsTabVisible`, set by MainViewModel navigation), `RefreshHelperLogCommand`, `CopyHelperLogCommand`, the 2s timer with the same busy-guard, `LogPath` (helper log path via `HelperLauncher.DefaultHelperLogPath()`). Constructor signature (append-only for the rest of the plan — later tasks add optional parameters at the end only): `(string? logDirectory = null, IPlatformServices? platform = null)` where null means the real logs dir / a throwing default (production always passes both explicitly; tests pass a temp dir). `CopyHelperLog` formats via `LogTail.FormatForDiagnostics` and copies through `IPlatformServices`. No bundle button, no crash section — Task 10 adds those to this VM and view; this task's deliverable (log viewer moved under Diagnostics) is complete without them.

- [ ] **Step 5: Port both views + wire nav**

`SettingsView.axaml` ports `SettingsView.xaml` minus the log-viewer section, with a `SaveButton` (`x:Name="SaveButton"`, `AutomationProperties.Name="Save settings"`). `DiagnosticsView.axaml` ports the log-viewer section with a `CopyLogButton` (`x:Name="CopyLogButton"`, `AutomationProperties.Name="Copy helper log"`). Every `{Binding X}` from the two WPF views must resolve on the Shell VMs (same grep-compare rule as Task 4). `App.axaml` gains both DataTemplates; `MainViewModel` gains both children + fan-out (`RefreshAllAdapters` touches Settings, `IsVisible` flips on navigate); `MainWindow.axaml` gains Settings + Diagnostics nav buttons.

- [ ] **Step 6: Run the tests + close the parity checklist**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "SettingsHeadlessTests|DiagnosticsHeadlessTests" -c Release`
Expected: PASS (4 tests).

Full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `259 passed, 0 failed` (255 + 4).

Manual sanity: launch unelevated; all four destinations render; Settings import/export round-trips; Diagnostics explains the missing log; Stop/attach semantics match WPF (compare side by side). Record the parity checklist in the commit message: single instance, attach, stop semantics, settings import/export, log viewer.

- [ ] **Step 7: Commit**

```bash
git add src/BlurLink.Shell tests/BlurLink.Core.Tests/SettingsHeadlessTests.cs tests/BlurLink.Core.Tests/DiagnosticsHeadlessTests.cs
git commit -m "Port Settings and Diagnostics to the Shell; parity checklist: single-instance, attach, stop, import/export, log viewer"
```

# Stage B — Feature bundles (spec order)

## Task 7: AOT re-run against the Shell

Task 11 of the foundation stage recorded the WPF `NETSDK1168` baseline and left the Avalonia row pending "after Task 4 of the surface plan" — parity is now further along (all four destinations live), so this task fills that row.

**Files:**
- Modify: `scripts/build-aot.ps1` (additive optional `-Project` parameter only)
- Modify: `docs/aot-spike.md` (Avalonia row: size, startup, staging/UAC verdict)

**Interfaces:**
- Consumes: Task 6's Shell (all destinations render); Task 11's script + docs table.
- Produces: the recorded AOT verdict for Avalonia; the default deliverable does not change whatever the result.

- [ ] **Step 1: Add the project parameter**

In `scripts/build-aot.ps1`, change the param block to:
```powershell
[CmdletBinding()]
param(
  [string]$Configuration = 'Release',
  [string]$Project = 'src/BlurLink.Desktop'
)
```
and change the publish line's `(Join-Path $root 'src/BlurLink.Desktop')` to `(Join-Path $root $Project)`. Nothing else changes; default behavior (WPF) is byte-identical.

Verify parse: `pwsh -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile('scripts/build-aot.ps1', [ref]$null, [ref]$errs); $errs.Count"` prints `0`.

- [ ] **Step 2: Run it against the Shell**

Run: `pwsh -NoProfile -File scripts/build-aot.ps1 -Project src/BlurLink.Shell`
Two honest outcomes, both acceptable — record whichever happens:
  - Publish succeeds: note `dist/BlurLink-Aot/BlurLink.exe` size MB, cold-start window ms (script prints both), then verify helper staging (`%LocalAppData%\BlurLink\bin` gains the helper after launch) and the UAC elevation prompt on Start. Record each.
  - Publish fails: keep the full error text (redact the local checkout path to `<repo>` per the privacy rule — the raw path trips `check-docs-privacy.ps1`'s machine-name rule).

- [ ] **Step 3: Fill the Avalonia row in `docs/aot-spike.md`**

Table columns (same as the WPF row): flavor, size, cold start, helper staging + UAC result, what failed. State explicitly which numbers are WPF-baseline and which are Avalonia. Restate the standing decision: slim framework-dependent stays the deliverable (spec D10); retiring `-Full`/`-Sfx` (D12) belongs to the M4 plan, not this one.

Run both checkers after editing: `scripts/check-docs-privacy.ps1` → `privacy-ok`, `scripts/check-doc-claims.ps1` → `claims-ok`.

- [ ] **Step 4: Commit**

```bash
git add scripts/build-aot.ps1 docs/aot-spike.md
git commit -m "Re-run the NativeAOT spike against the Avalonia shell and record it"
```

## Task 8: Guided first run + verified badge

**Files:**
- Create: `src/BlurLink.Core/FirstRun/BlurFinder.cs`, `src/BlurLink.Core/FirstRun/VerifiedProfileWriter.cs`
- Modify: `src/BlurLink.Contracts/BlurLinkConfig.cs` (`VerifiedProfileName`, `VerifiedProfileDate`, `FirstRunDismissed`, `HostIpByProfile`), `src/BlurLink.Core/Config/BlurLinkConfigStore.cs` (`Migrate` defaults)
- Create: `src/BlurLink.Shell/ViewModels/FirstRunViewModel.cs`, `src/BlurLink.Shell/Views/FirstRunView.axaml(.cs)`
- Modify: `src/BlurLink.Shell/ViewModels/MainViewModel.cs` (`ShowFirstRun`, FirstRun child), `Views/MainWindow.axaml` (overlay)
- Test: `tests/BlurLink.Core.Tests/FirstRunTests.cs`

**Interfaces:**
- Consumes: Task 4's settings/sniff VMs (step states read them, never duplicate them — R4); `GameProfile`/`GameProfileStore`; `HexSignatureParser`.
- Produces: `BlurFinder` candidates, `VerifiedProfileWriter.Write` path, config badge fields, `FirstRunViewModel` steps + badge; Task 11 reads `HostIpByProfile`.

First run appears once: `ShowFirstRun` is true when `VerifiedProfileDate` is empty AND `DiscoveryUdpPort` is null AND `FirstRunDismissed` is false. [Skip] sets `FirstRunDismissed` and saves. Research mode survives as Advanced manual entry — untouched.

- [ ] **Step 1: Write the failing tests**

`tests/BlurLink.Core.Tests/FirstRunTests.cs`:
```csharp
using BlurLink.Contracts;
using BlurLink.Core.FirstRun;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class FirstRunTests
{
    [Fact]
    public void Writer_NamesTheFile_ByDate_AndAvoidsCollisions()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var profile = new GameProfile
        {
            ProfileName = "Blur LAN (verified 2026-09-14)",
            DiscoveryUdpPort = 50001,
            BroadcastDestination = "255.255.255.255",
            PayloadPrefixHex = "0F 00 00",
            Notes = "unit fixture",
        };

        var first = VerifiedProfileWriter.Write(profile, dir);
        var second = VerifiedProfileWriter.Write(profile, dir);

        Assert.Equal(Path.Combine(dir, "blur-lan-verified-2026-09-14.json"), first);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.Contains("verified 2026-09-14", File.ReadAllText(first));
    }

    [Fact]
    public void Writer_Refuses_AnUnverifiedProfile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        Assert.Throws<ArgumentException>(() =>
            VerifiedProfileWriter.Write(GameProfile.ResearchMode(), dir));
    }

    [Fact]
    public void Finder_AlwaysReturns_AList_NeverThrows()
    {
        var candidates = BlurFinder.Candidates();

        Assert.NotNull(candidates);
    }

    [Fact]
    public void Badge_ShowsResearchMode_UntilAProfileIsWritten()
    {
        var config = BlurLinkConfig.CreateDefault();

        Assert.Equal("Research mode", FirstRunBadge.Text(config));

        config.VerifiedProfileName = "Blur LAN (verified 2026-09-14)";
        config.VerifiedProfileDate = "2026-09-14";

        Assert.Equal("Verified 2026-09-14", FirstRunBadge.Text(config));
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter FirstRunTests -c Release`
Expected: FAIL — `VerifiedProfileWriter`, `BlurFinder`, `FirstRunBadge` do not exist.

- [ ] **Step 3: Implement finder, writer, badge, config fields**

`src/BlurLink.Core/FirstRun/BlurFinder.cs` — best-effort candidates, never throws, slowest source last:
```csharp
using System.Diagnostics;

namespace BlurLink.Core.FirstRun;

/// <summary>Best-effort Blur.exe location candidates for first run step 1.
/// Order: running process, Steam library scan, registry Uninstall scan.
/// Returns paths that exist right now; empty is a normal answer (Browse covers it).</summary>
public static class BlurFinder
{
    public static IReadOnlyList<string> Candidates()
    {
        var found = new List<string>();
        try { found.AddRange(FromRunningProcess()); } catch { }
        try { found.AddRange(FromSteamLibraries()); } catch { }
        try { found.AddRange(FromUninstallRegistry()); } catch { }
        return found.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FromRunningProcess()
    {
        foreach (var p in Process.GetProcessesByName("Blur"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            finally { p.Dispose(); }
            if (!string.IsNullOrEmpty(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> FromSteamLibraries()
    {
        foreach (var steamDir in SteamDirs())
        {
            var common = Path.Combine(steamDir, "steamapps", "common");
            if (!Directory.Exists(common))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(common))
            {
                var candidate = Path.Combine(dir, "Blur.exe");
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> SteamDirs()
    {
        yield return @"C:\Program Files (x86)\Steam";
        yield return @"C:\Program Files\Steam";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
        catch { }
    }

    private static IEnumerable<string> FromUninstallRegistry()
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            string[] subkeys;
            try { subkeys = hive.OpenSubKey(uninstall)?.GetSubKeyNames() ?? Array.Empty<string>(); }
            catch { continue; }
            foreach (var sub in subkeys)
            {
                try
                {
                    using var key = hive.OpenSubKey(Path.Combine(uninstall, sub));
                    var name = key?.GetValue("DisplayName") as string;
                    var location = key?.GetValue("InstallLocation") as string;
                    if (name is not null && name.Contains("Blur", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(location))
                    {
                        var candidate = Path.Combine(location, "Blur.exe");
                        if (File.Exists(candidate))
                        {
                            yield return candidate;
                        }
                    }
                }
                catch { }
            }
        }
    }
}
```

`src/BlurLink.Core/FirstRun/VerifiedProfileWriter.cs`:
```csharp
using BlurLink.Contracts;
using BlurLink.Core.Config;

namespace BlurLink.Core.FirstRun;

/// <summary>Writes a dated verified profile. Refuses research-mode (null port):
/// a file named "verified" must contain verified values.</summary>
public static class VerifiedProfileWriter
{
    public static string Write(GameProfile profile, string directory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.DiscoveryUdpPort is not int port || port is < 1 or > 65535)
        {
            throw new ArgumentException("A verified profile needs a verified discovery port.", nameof(profile));
        }

        Directory.CreateDirectory(directory);
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"blur-lan-verified-{date}.json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"blur-lan-verified-{date}-{n}.json");
        }

        File.WriteAllText(path, GameProfileStore.Export(profile));
        return path;
    }
}
```

`src/BlurLink.Core/FirstRun/FirstRunBadge.cs`:
```csharp
using BlurLink.Contracts;

namespace BlurLink.Core.FirstRun;

public static class FirstRunBadge
{
    public static string Text(BlurLinkConfig config)
        => string.IsNullOrWhiteSpace(config.VerifiedProfileDate)
            ? "Research mode"
            : $"Verified {config.VerifiedProfileDate.Trim()}";
}
```

`BlurLinkConfig` gains (exact, with `JsonPropertyName`s matching field names):
```csharp
[JsonPropertyName("verifiedProfileName")]
public string VerifiedProfileName { get; set; } = string.Empty;

[JsonPropertyName("verifiedProfileDate")]
public string VerifiedProfileDate { get; set; } = string.Empty;

[JsonPropertyName("firstRunDismissed")]
public bool FirstRunDismissed { get; set; }

/// <summary>Profile name → host overlay IP remembered for it (R8: this dict only).</summary>
[JsonPropertyName("hostIpByProfile")]
public Dictionary<string, string> HostIpByProfile { get; set; } = new();
```
`BlurLinkConfigStore.Migrate` gains: `cfg.VerifiedProfileName ??= string.Empty; cfg.VerifiedProfileDate ??= string.Empty; cfg.HostIpByProfile ??= new();`

- [ ] **Step 4: Implement the FirstRun VM + view + overlay**

`FirstRunViewModel(ShellViewModelBase)` — one `StepState` record per spec §7.1 step, computed from live collaborators it is given (never duplicated logic):
```csharp
public sealed record FirstRunStep(string Title, string Detail, string ActionLabel, bool Done, string ActionView);
```
Constructor: `(BlurLinkConfig config, BridgeSettingsViewModel settings, Func<IReadOnlyList<string>> findBlur, Action<string> navigate, Action onChanged)`. Steps:
1. "Find Blur" — done when `File.Exists(config.BlurExePath)`; action "Locate…" → sets config path from `findBlur().FirstOrDefault()` or opens Browse via navigate("Settings"); detail names the found path or "not found yet".
2. "Choose the overlay" — done when `settings.SelectedAdapter is not null && settings.PreflightReady`-route-part? Use `settings.RouteWarning == string.Empty && settings.SelectedAdapter is not null`; action → navigate("Join").
3. "Verify the discovery port" — done when `settings.DiscoveryPort` parses 1–65535; action → navigate("Join").
4. "Pre-flight" — done when `settings.PreflightReady`; detail is `settings.PreflightSummary`; action "Write verified profile" writes via `VerifiedProfileWriter` to `%LocalAppData%\BlurLink\profiles`, sets `VerifiedProfileName/Date`, remembers `HostIpByProfile[name] = hostIp`, calls `onChanged`, or "Start" when written.
`VerifiedBadge => FirstRunBadge.Text(config)`. `[Skip]` → `config.FirstRunDismissed = true; onChanged();`.

`MainViewModel.ShowFirstRun => string.IsNullOrWhiteSpace(Config.VerifiedProfileDate) && Config.DiscoveryUdpPort is null && !Config.FirstRunDismissed`. `MainWindow.axaml` shows `FirstRunView` overlay (bound to a `FirstRun` child on MainViewModel) when `ShowFirstRun`, with Esc bound to Skip (`KeyBinding Gesture="Escape" Command="{Binding FirstRun.SkipCommand}"`). Every button keyboard-reachable by construction; `AutomationProperties.Name` on all four action buttons + Skip.

Headless test (add to `FirstRunTests.cs` — needs `[AvaloniaFact]`, same file):
```csharp
[AvaloniaFact]
public void FreshConfig_ShowsAllStepsUndone_AndSkipDismisses()
{
    var config = BlurLinkConfig.CreateDefault();
    var settings = new BridgeSettingsViewModel(config, () => { }, () => { });
    var vm = new FirstRunViewModel(config, settings, () => new[] { @"C:\Games\Blur\Blur.exe" }, _ => { }, () => { });
    var view = new FirstRunView { DataContext = vm };
    view.Show();

    try
    {
        Assert.All(vm.Steps, s => Assert.False(s.Done));
        Assert.Equal("Research mode", vm.VerifiedBadge);
        vm.SkipCommand.Execute(null);
        Assert.True(config.FirstRunDismissed);
    }
    finally
    {
        view.Close();
    }
}
```
`BridgeSettingsViewModel` constructor here is `(config, onChanged, capabilitiesChanged)` per Task 4 — use exactly that. `SkipCommand` must exist on the VM.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter FirstRunTests -c Release`
Expected: PASS (5 tests).

Full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `264 passed, 0 failed` (259 + 5).

Manual sanity: delete `%LocalAppData%\BlurLink\settings.json` (back it up first), launch — first-run overlay appears; Esc dismisses; restore the backup.

- [ ] **Step 6: Commit**

```bash
git add src/BlurLink.Core/FirstRun src/BlurLink.Contracts/BlurLinkConfig.cs src/BlurLink.Core/Config/BlurLinkConfigStore.cs src/BlurLink.Shell tests/BlurLink.Core.Tests/FirstRunTests.cs
git commit -m "Guided first run with a verified badge and a dated verified-profile writer"
```

## Task 9: Verify-profile tool + prefix stability

**Files:**
- Create: `src/BlurLink.Core/Verify/PrefixStability.cs`
- Create: `src/BlurLink.Shell/ViewModels/VerifyProfileViewModel.cs`
- Modify: `src/BlurLink.Shell/ViewModels/DiagnosticsViewModel.cs` (+ Verify section child), `Views/DiagnosticsView.axaml` (+ section)
- Test: `tests/BlurLink.Core.Tests/PrefixStabilityTests.cs` (+ one headless test for the section)

**Interfaces:**
- Consumes: Task 8's writer; `HexSignatureParser.Parse`; Diagnostics home from Task 6.
- Produces: 12-byte stability rule (settles spec Q3), verify UI writing real profiles.

- [ ] **Step 1: Write the failing tests**

`tests/BlurLink.Core.Tests/PrefixStabilityTests.cs`:
```csharp
using BlurLink.Core.Verify;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class PrefixStabilityTests
{
    [Fact]
    public void ThreeAgreeingSamples_ReturnTheTwelveByteRun()
    {
        var result = PrefixStability.Check(new[]
        {
            "0F 00 00 00 00 00 00 2C 01 00 00 00 AA BB CC DD",
            "0f0000000000002c0100000011223344",
            "0F 00 00 00 00 00 00 2C 01 00 00 00 99 88 77 66",
        });

        Assert.True(result.Agreed);
        Assert.Equal("0F 00 00 00 00 00 00 2C 01 00 00 00", result.PrefixHex);
    }

    [Fact]
    public void OneDifferingByte_RefusesWithAReason()
    {
        var result = PrefixStability.Check(new[]
        {
            "0F 00 00 00 00 00 00 2C 01 00 00 00 AA",
            "1F 00 00 00 00 00 00 2C 01 00 00 00 BB",
            "0F 00 00 00 00 00 00 2C 01 00 00 00 CC",
        });

        Assert.False(result.Agreed);
        Assert.NotEmpty(result.Reason);
        Assert.Equal(string.Empty, result.PrefixHex);
    }

    [Fact]
    public void FewerThanThreeSamples_NeverAgrees()
    {
        var result = PrefixStability.Check(new[] { "0F 00 00", "0F 00 00" });

        Assert.False(result.Agreed);
    }

    [Fact]
    public void GarbageSamples_AreIgnored_NotFatal()
    {
        var result = PrefixStability.Check(new[] { "not hex", "", "0F 00 00 00 00 00 00 2C 01 00 00 00" });

        Assert.False(result.Agreed);
        Assert.Contains("need", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter PrefixStabilityTests -c Release`
Expected: FAIL — `PrefixStability` does not exist.

- [ ] **Step 3: Implement the 12-byte rule**

`src/BlurLink.Core/Verify/PrefixStability.cs`:
```csharp
using BlurLink.Core.Validation;

namespace BlurLink.Core.Verify;

public sealed record PrefixStabilityResult(bool Agreed, string PrefixHex, string Reason);

/// <summary>Three-capture stability rule (spec Q3, settled at 12 bytes): the first
/// 12 payload bytes must be identical across at least three parseable samples
/// before they may become a profile prefix. Pasted from the user's own Wireshark —
/// the sniffer is metadata-only and never yields payload bytes (R5).</summary>
public static class PrefixStability
{
    public const int RequiredSamples = 3;
    public const int PrefixLength = 12;

    public static PrefixStabilityResult Check(IReadOnlyList<string> samples)
    {
        var parsed = new List<byte[]>();
        foreach (var sample in samples)
        {
            try
            {
                var bytes = HexSignatureParser.Parse(sample);
                if (bytes.Length >= PrefixLength)
                {
                    parsed.Add(bytes);
                }
            }
            catch
            {
                // garbage is ignored, never fatal — it just does not count
            }
        }

        if (parsed.Count < RequiredSamples)
        {
            return new PrefixStabilityResult(false, string.Empty,
                $"Need {RequiredSamples} parseable samples of {PrefixLength}+ bytes; got {parsed.Count}.");
        }

        var first = parsed[0].Take(PrefixLength).ToArray();
        for (var i = 1; i < parsed.Count; i++)
        {
            if (!parsed[i].Take(PrefixLength).SequenceEqual(first))
            {
                return new PrefixStabilityResult(false, string.Empty,
                    $"Sample {i + 1} differs inside the first {PrefixLength} bytes — leave the signature empty.");
            }
        }

        return new PrefixStabilityResult(true,
            string.Join(" ", first.Select(b => b.ToString("X2"))),
            "Stable across captures.");
    }
}
```

- [ ] **Step 4: Implement the Verify section in Diagnostics**

`VerifyProfileViewModel(ShellViewModelBase)`: `Sample1/2/3` strings, `CheckCommand` → `Result` text (`PrefixStability.Check`), `ProfileName` (default `"Blur LAN (verified {yyyy-MM-dd})"` filled at check time), `WriteCommand` (enabled when agreed; writes via `VerifiedProfileWriter` to the app-data profiles dir, sets config verified fields + current Join port/broadcast/prefix through callbacks `applyPort(string)`, `applyBroadcast(string)`, `applyPrefix(string)`, remembers host per R8, calls `onChanged`; result line confirms the path). Constructor: `(BlurLinkConfig config, Action<string> applyPort, Action<string> applyBroadcast, Action<string> applyPrefix, Action onChanged)`.

`DiagnosticsViewModel` gains a `Verify` child: give `DiagnosticsViewModel` an append-only optional parameter `VerifyProfileViewModel? verify = null` (existing constructions, including the Task 6 tests, compile unchanged passing nothing) and have `MainViewModel` construct the real `VerifyProfileViewModel` with the Join settings callbacks and pass it in. `DiagnosticsView.axaml` gains the section: three `TextBox`es (`Watermark="Paste first 16+ payload bytes from Wireshark"`), Check button (`AutomationProperties.Name="Check prefix stability"`), result `TextBlock` (`x:Name="VerifyResult"`), Write button. Headless test asserts a disagreeing pair renders the refusal reason in `VerifyResult`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "PrefixStabilityTests|DiagnosticsHeadlessTests" -c Release`
Expected: PASS (4 + the Task 6 diagnostics tests).

Full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `269 passed, 0 failed` (264 + 5: 4 stability + 1 headless).

- [ ] **Step 6: Commit**

```bash
git add src/BlurLink.Core/Verify src/BlurLink.Shell tests/BlurLink.Core.Tests/PrefixStabilityTests.cs
git commit -m "Verify-profile tool with the 12-byte three-capture stability rule"
```

## Task 10: Support bundle + crash capture

**Files:**
- Create: `src/BlurLink.Core/Support/SupportBundle.cs`, `src/BlurLink.Core/Support/CrashLog.cs`
- Modify: `src/BlurLink.Shell/App.axaml.cs` (crash hooks), `src/BlurLink.Shell/ViewModels/DiagnosticsViewModel.cs` (+ bundle + crash tail), `Views/DiagnosticsView.axaml` (+ two sections)
- Test: `tests/BlurLink.Core.Tests/SupportBundleTests.cs`

**Interfaces:**
- Consumes: Task 6's Diagnostics home; `LogTail`, `AppLog.DefaultPath()`, `BlurLinkConfigStore.ExportJson`, `DiagnosticsCollector.BuildDiagnosticsText` signature `(string hostOverlayIp, int? discoveryUdpPort, int selectedAdapterIfIndex)`.
- Produces: one-click bundle zip + crash log; Task 13's support story points here.

- [ ] **Step 1: Write the failing tests**

`tests/BlurLink.Core.Tests/SupportBundleTests.cs`:
```csharp
using BlurLink.Core.Support;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class SupportBundleTests
{
    private static string FixtureDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "blurlink.log"), "line one\nline two\n");
        File.WriteAllText(Path.Combine(dir, "helper.log"), "helper started\n");
        File.WriteAllText(Path.Combine(dir, "evil.cap"), "must never be bundled");
        File.WriteAllText(Path.Combine(dir, "payload.bin"), "must never be bundled");
        return dir;
    }

    [Fact]
    public void Create_BundlesLogsDiagnosticsAndConfig_ButNeverCaptures()
    {
        var dir = FixtureDir();
        var dest = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var zip = SupportBundle.Create(dir, "diagnostics text", """{"hostOverlayIp":"10.0.0.10"}""", dest);

        Assert.True(File.Exists(zip));
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.Name).ToList();
        Assert.Contains("blurlink.log", names);
        Assert.Contains("helper.log", names);
        Assert.Contains("diagnostics.txt", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("versions.txt", names);
        Assert.DoesNotContain("evil.cap", names);
        Assert.DoesNotContain("payload.bin", names);
    }

    [Fact]
    public void Create_Succeeds_WithAnEmptyLogDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "logs");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var zip = SupportBundle.Create(dir, "diagnostics text", "{}", dest);

        Assert.True(File.Exists(zip));
    }

    [Fact]
    public void CrashLog_Writes_TypeMessageAndStack()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var ex = new InvalidOperationException("boom");

        var path = CrashLog.Write(dir, ex);
        var text = File.ReadAllText(path);

        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom", text);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "SupportBundleTests" -c Release`
Expected: FAIL — `SupportBundle`, `CrashLog` do not exist.

- [ ] **Step 3: Implement bundle + crash log**

`src/BlurLink.Core/Support/SupportBundle.cs`:
```csharp
using System.IO.Compression;

namespace BlurLink.Core.Support;

/// <summary>One-click support bundle: logs + diagnostics metadata, zipped.
/// Only *.log files are ever collected (plus the three generated files), so a
/// stray capture placed in the logs dir cannot enter a bundle by construction.</summary>
public static class SupportBundle
{
    public static string Create(string logsDir, string diagnosticsText, string configJson, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(destDir, $"support-bundle-{stamp}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "*.log").OrderBy(f => f))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        AddText(zip, "diagnostics.txt", diagnosticsText);
        AddText(zip, "settings.json", configJson);
        AddText(zip, "versions.txt",
            $"BlurLink={typeof(SupportBundle).Assembly.GetName().Version} " +
            $"os={Environment.OSVersion} x64={Environment.Is64BitProcess}");
        return zipPath;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
```

`src/BlurLink.Core/Support/CrashLog.cs`:
```csharp
namespace BlurLink.Core.Support;

/// <summary>Local crash capture. View-model level only — no packet data can
/// reach it, so the metadata-only logging discipline holds trivially.</summary>
public static class CrashLog
{
    public static string Write(string dir, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(dir, $"crash-{stamp}.log");
        var frames = (ex.StackTrace ?? "(no stack)").Split('\n').Take(20);
        File.WriteAllText(path,
            $"[{DateTime.UtcNow:O}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}" +
            string.Join(Environment.NewLine, frames));
        return path;
    }

    public static string? Newest(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, "crash-*.log")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
```

- [ ] **Step 4: Hook crashes + surface bundle and crash tail in Diagnostics**

`App.axaml.cs` gains, before creating the main window:
```csharp
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try { CrashLog.Write(LogsDir(), e.ExceptionObject as Exception ?? new Exception("unknown")); } catch { }
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try { CrashLog.Write(LogsDir(), e.Exception); } catch { }
    e.SetObserved();
};
Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
{
    try { CrashLog.Write(LogsDir(), e.Exception); } catch { }
};
```
with `private static string LogsDir() => Path.GetDirectoryName(Core.Logging.AppLog.DefaultPath()) ?? Path.GetTempPath();`

`DiagnosticsViewModel` gains: `BundleResult` string, `BundleCommand` (builds diagnostics text via `DiagnosticsCollector.BuildDiagnosticsText(config.HostOverlayIp, config.DiscoveryUdpPort, config.SelectedAdapterIfIndex)`, exports config via `BlurLinkConfigStore.ExportJson(config)`, calls `SupportBundle.Create(logsDir, text, json, destDir)` into `%LocalAppData%\BlurLink\bundles`, sets `BundleResult` to the path or the error — append `BlurLinkConfig? config = null` as a third optional constructor parameter (existing constructions compile unchanged; when null the command reports "configuration unavailable" instead of throwing), `CrashTail` string (newest crash file tail-20 via `LogTail.Read`, or "No crashes recorded."). `DiagnosticsView.axaml` gains both sections with `AutomationProperties.Name="Create support bundle"` on the button.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "SupportBundleTests|DiagnosticsHeadlessTests" -c Release`
Expected: PASS (3 + Task 6 diagnostics tests).

Full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `272 passed, 0 failed` (269 + 3).

- [ ] **Step 6: Commit**

```bash
git add src/BlurLink.Core/Support src/BlurLink.Shell tests/BlurLink.Core.Tests/SupportBundleTests.cs
git commit -m "One-click support bundle and local crash capture under Diagnostics"
```

## Task 11: Everyday polish

**Files:**
- Create: `src/BlurLink.Shell/Notifications/SessionNotifier.cs`
- Modify: `src/BlurLink.Shell/ViewModels/MainViewModel.cs` (subscribe, tooltip, host memory), `Views/MainWindow.axaml(.cs)` (banner already live from Task 4 — dot binding only), `App.axaml.cs` (tray icon + menu)
- Test: `tests/BlurLink.Core.Tests/SessionNotifierTests.cs` (+ headless banner test)

**Interfaces:**
- Consumes: Tasks 3–4, 8 (`NotificationCenter`, session `StateChanged` events, `HostIpByProfile`, story codes).
- Produces: tray state, transition notices, per-profile host memory, attach/auto-stop parity proof.

Tray icon is static art with live text (R7): tooltip mirrors the session story, menu offers Show + Quit + Copy diagnostics. No pixel variants, no balloon API.

- [ ] **Step 1: Write the failing tests**

`tests/BlurLink.Core.Tests/SessionNotifierTests.cs`:
```csharp
using BlurLink.Shell.Notifications;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class SessionNotifierTests
{
    [Theory]
    [InlineData("bridge-idle", "bridge-forwarding", true)]
    [InlineData("bridge-forwarding", "bridge-forwarding", false)]
    [InlineData("host-waiting", "host-forwarding", true)]
    [InlineData("idle", "blocked", true)]
    [InlineData("idle", "idle", false)]
    public void Watch_EmitsOnce_PerTransition(string prev, string next, bool expected)
    {
        var notice = SessionNotifier.Watch(prev, next);

        Assert.Equal(expected, notice is not null);
    }

    [Fact]
    public void ForwardingNotice_PointsAtHostMode()
    {
        var notice = SessionNotifier.Watch("bridge-idle", "bridge-forwarding");

        Assert.NotNull(notice);
        Assert.Contains("Host mode", notice.Message);
    }

    [Fact]
    public void LostConnection_IsAnError()
    {
        var notice = SessionNotifier.Watch("bridge-forwarding", "failed");

        Assert.NotNull(notice);
        Assert.Equal(NoticeSeverity.Error, notice.Severity);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/BlurLink.Core.Tests --filter SessionNotifierTests -c Release`
Expected: FAIL — `SessionNotifier` does not exist.

- [ ] **Step 3: Implement the transition table + wiring**

`src/BlurLink.Shell/Notifications/SessionNotifier.cs`:
```csharp
namespace BlurLink.Shell.Notifications;

/// <summary>Pure transition table: previous story code + next story code → a
/// `Notice` for the center, or null when nothing worth interrupting happened.
/// Fires once per transition — steady states never re-notify. Returns the
/// center's own record type so MainViewModel pushes it without mapping.</summary>
public static class SessionNotifier
{
    public static Notice? Watch(string prevCode, string nextCode)
    {
        if (string.Equals(prevCode, nextCode, StringComparison.Ordinal))
        {
            return null;
        }

        return nextCode switch
        {
            "bridge-forwarding" => new Notice("Lobby may appear",
                "You are reaching the host. If the lobby does not appear, the host may need Host mode.",
                NoticeSeverity.Progress, DateTime.UtcNow),
            "host-forwarding" => new Notice("Replies on their way",
                "Your Blur answered, and replies are being sent back over the overlay.",
                NoticeSeverity.Progress, DateTime.UtcNow),
            "failed" => new Notice("Session stopped with an error",
                "Open Diagnostics for the helper log, then start again.",
                NoticeSeverity.Error, DateTime.UtcNow),
            "blocked" => new Notice("Not ready yet",
                "A requirement is missing — see the session story for the fix.",
                NoticeSeverity.Warning, DateTime.UtcNow),
            _ => null,
        };
    }
}
```

`MainViewModel` wiring (exact): keep `prevCode` per session VM; subscribe to each session VM's forwarded `StateChanged` (add `public event Action<SessionState>? StateChanged` passthroughs on the Join session part and Host VM if missing — forward the coordinator event, one line each); on fire, compute `SessionStoryTable.Describe(state).Code`, call `SessionNotifier.Watch`, push into `NotificationCenter`, and set `TrayToolTip` (`$"BlurLink — {story.Headline}"`, default `"BlurLink — idle"`). Per-profile host memory: after a successful bridge/host start, `Config.HostIpByProfile[ActiveProfileName()] = Config.HostOverlayIp` where `ActiveProfileName()` returns `VerifiedProfileName` or `"Research mode"`; when a verified profile is applied (Task 8/9 writers), prefill `HostIp` from the dict when present. Test the dict behavior in `SessionNotifierTests.cs`? No — separate `[Fact]`s in the same file against a `static string? RecallHost(BlurLinkConfig c)` helper on `MainViewModel`? Put the two helpers as `internal static` on MainViewModel: `ActiveProfileName(BlurLinkConfig)` + `RememberHost(BlurLinkConfig)`; test them directly.

Attach/auto-stop parity: port the three WPF behaviors into the Shell session part if Task 4 missed any (game-folder `WorkingDirectory`, `BlurArgs` passed, exit code/duration in `Message`, auto-stop on exit unless opted out), and assert each with a test in `JoinHeadlessTests.cs` style — name them `Launch_UsesGameFolderAsWorkdir`, `BlurExit_AutoStops_WhenEnabled`, `BlurExit_KeepsBridge_WhenOptedOut`. If Task 4 already covered them, this step only adds the missing ones — say which in the report.

Tray in `App.axaml.cs` (exact, after MainWindow creation):
```csharp
var tray = new TrayIcon
{
    Icon = new WindowIcon("Assets/app.ico"),
    ToolTipText = "BlurLink — idle",
    IsVisible = true,
};
var menu = new NativeMenu();
var show = new NativeMenuItem("Show BlurLink");
show.Click += (_, _) => { window.Show(); window.Activate(); };
var quit = new NativeMenuItem("Quit");
quit.Click += (_, _) => { window.Close(); };
menu.Items.Add(show);
menu.Items.Add(quit);
tray.Menu = menu;
TrayIcon.SetIcons(this, new TrayIcons { tray });
vm.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(MainViewModel.TrayToolTip))
    {
        tray.ToolTipText = vm.TrayToolTip;
    }
};
```
(`TrayIcon`, `TrayIcons`, `NativeMenu`, `NativeMenuItem` are in `Avalonia.Controls`; `WindowIcon` in `Avalonia`. If 12.x renamed any member, use the compiler-suggested one and record it — build is the check.)

Headless banner test (append to `JoinHeadlessTests.cs`):
```csharp
[AvaloniaFact]
public void Dismiss_RemovesTheBanner()
{
    var center = new BlurLink.Shell.Notifications.NotificationCenter();
    center.Notify("t", "m", BlurLink.Shell.Notifications.NoticeSeverity.Info);
    var main = new MainView { ... }
```
No — MainWindow needs the full MainViewModel (heavy: config store, adapters). Instead test the banner binding on a bare `ItemsControl`: skip it; the notifier table + tooltip tests carry this task. Banner rendering was already proven by Task 4's `ItemsControl` in the smoke path — extend `ShellSmokeTests`? There is none; Task 3 tested Dialog only. Add one headless assertion to `HostHeadlessTests`? Out of scope creep. Decision: no banner test; banner XAML is static markup exercised by every headless `Show()` (a binding typo throws at layout — Avalonia binding errors do NOT throw by default...). Hmm, honesty: state in the report that banner dismissal is covered by NotificationCenter unit tests (Dismiss removes → ItemsControl updates via INPC, the same mechanism every passing headless render uses). Do not claim more.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/BlurLink.Core.Tests --filter "SessionNotifierTests" -c Release`
Expected: PASS (5 theory cases + 2 facts + 2 host-memory facts = 9).

Full suite: `dotnet test BlurLink.sln -c Release --nologo`
Expected: `281 passed, 0 failed` (272 + 9).

- [ ] **Step 5: Commit**

```bash
git add src/BlurLink.Shell tests/BlurLink.Core.Tests/SessionNotifierTests.cs
git commit -m "Everyday polish: tray state, transition notices, per-profile host memory"
```

## Task 12: Correctness extras

**Files:**
- Modify: `src/BlurLink.Core/Net/WinDivertFilterBuilder.cs` (+ `AdapterIfIndex`), `HostFilterBuilder.cs` (+ optional ifIdx), `src/BlurLink.Contracts/IpcMessages.cs` (+ shape-expectation fields + status counters), `src/BlurLink.Contracts/BlurLinkConfig.cs`? No — config unchanged.
- Modify: `src/BlurLink.Net/include/blurlink/announce.h`, `src/announce.cpp` (`BuildHostFilterString` + ifIdx), `src/BlurLink.Net/include/blurlink/config.h` + `src/config.cpp` (bridge filter + `adapter_if_index`), helper status emission (new counters), reply-shape check in bridge + host-session reply paths.
- Create: `src/BlurLink.Core/Net/ReplyShapeValidator.cs`, `src/BlurLink.Shell/Diagnostics/RefusalExplainer.cs` (or `ViewModels/RefusalExplainer.cs` — put it beside the Diagnostics VM: `src/BlurLink.Shell/ViewModels/RefusalExplainer.cs`)
- Modify: `src/BlurLink.Core/Session/SessionState.cs` (+ 2 counters), `src/BlurLink.Shell/ViewModels/DiagnosticsViewModel.cs` + `Views/DiagnosticsView.axaml` (+ shape + refusals sections), `scripts/test-e2e.ps1` (reported-filter assertions)
- Test: `tests/BlurLink.Core.Tests/FilterIfIndexTests.cs`, `ReplyShapeValidatorTests.cs`, `RefusalExplainerTests.cs`, native `TestFilter`/`TestHostFilter`/`TestReplyShape` additions, `scripts/test-interop.ps1` (+ 2 contract checks)

**Interfaces:**
- Consumes: all prior tasks; `IpcStartRequest.AdapterIfIndex` (already parsed — verify, else parse); real filter-string shapes from the current builders.
- Produces: adapter-constraining filters, observe-only shape counters, readable refusals. Nothing here blocks or drops a packet that flowed before.

- [ ] **Step 1: `ifIdx` in the managed builders (failing tests first)**

`tests/BlurLink.Core.Tests/FilterIfIndexTests.cs`:
```csharp
using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class FilterIfIndexTests
{
    [Fact]
    public void BridgeFilter_WithoutAdapter_HasNoIfIdxTerm()
    {
        var filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(50001, "255.255.255.255"));

        Assert.DoesNotContain("ifIdx", filter);
    }

    [Fact]
    public void BridgeFilter_WithAdapter_ScopesToIt()
    {
        var filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(50001, "255.255.255.255") with { AdapterIfIndex = 22 });

        Assert.Contains("ifIdx == 22", filter);
    }

    [Fact]
    public void HostFilter_WithAdapter_ScopesAllThreeTerms()
    {
        var filter = HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("192.168.0.200", 50001) }, adapterIfIndex: 22);

        Assert.Equal(3, filter.Split("ifIdx == 22").Length - 1);
    }

    [Fact]
    public void HostFilter_WithoutAdapter_IsUnchanged()
    {
        var filter = HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("192.168.0.200", 50001) });

        Assert.DoesNotContain("ifIdx", filter);
    }
}
```

Run: `dotnet test tests/BlurLink.Core.Tests --filter FilterIfIndexTests -c Release` → FAIL (`AdapterIfIndex`, `adapterIfIndex` do not exist).

Implement: `FilterInput` gains `int? AdapterIfIndex = null`; `Build` appends `$" && ifIdx == {n}"` when `AdapterIfIndex is > 0`. `HostFilterBuilder.Build(int discoveryPort, IReadOnlyList<HostPlayerKey> players, int? adapterIfIndex = null)` appends the same term to each of the three terms when `> 0`. Old call sites compile unchanged (optional/default). Only canonical numeric interpolation — the no-concatenation rule holds (`int` formatting only).

- [ ] **Step 2: `ifIdx` in the native helper + harness assertion**

Mirror the rule in C++: `BuildHostFilterString(int discovery_port, const std::vector<std::array<std::uint8_t,4>>& player_lans, std::string& error, int adapter_if_index = 0)` appends ` && ifIdx == N` per term when `N > 0`; `BridgeConfig` (in `include/blurlink/config.h`) gains `int adapter_if_index = 0`, fed from the already-parsed `AdapterIfIndex` IPC field into `BuildFilterString` the same way. Extend the native filter tests (`TestFilter`, host-filter cases in `tests/BlurLink.Net.Tests/src/test_main.cpp`) with with/without cases mirroring Step 1. Run: `ctest --test-dir out/native-tests` green under the existing CMake flow (`scripts/test.ps1` documents it).

`scripts/test-e2e.ps1`: in the `bridge-reports-its-own-filter` stage and the host `host-filter-is-host-modes-own` stage, extend the existing filter assertions with the `ifIdx` term for the harness adapter (resolve its ifIndex once at the top via the same adapter the script already binds; assert `-match 'ifIdx == <n>'`). Run the parser check unelevated (`[Parser]::ParseFile`, 0 errors). The elevated proof stays honestly pending: note in the report that the new assertions run at the next elevated driver run, exactly like every other e2e stage.

- [ ] **Step 3: Reply-shape validation, observe-only (failing tests first)**

`IpcStartRequest` and `IpcStartHostRequest` gain:
```csharp
/// <summary>Observe-only reply-shape expectation from the verified profile. Null/empty = off.</summary>
public int? ExpectedReplyLength { get; set; }
public string ExpectedReplyPrefixHex { get; set; } = string.Empty;
```
`IpcStatusResponse` gains `long ReplyShapeChecked` + `long ReplyShapeMismatch` (defaults 0).

`src/BlurLink.Core/Net/ReplyShapeValidator.cs` (the rule, shared by reasoning if not by binary):
```csharp
namespace BlurLink.Core.Net;

/// <summary>Observe-only reply-shape rule (R6: length + leading prefix; nonce-echo
/// needs query memory the helper does not keep, so it is explicitly not here).
/// Never throws on garbage; empty expectations mean "off".</summary>
public static class ReplyShapeValidator
{
    public static bool Matches(int actualLength, byte[] actualLeading, int? expectedLength, byte[]? expectedPrefix)
    {
        if (expectedLength is null && (expectedPrefix is null || expectedPrefix.Length == 0))
        {
            return true;
        }

        if (expectedLength is int len && actualLength != len)
        {
            return false;
        }

        if (expectedPrefix is { Length: > 0 } prefix)
        {
            if (actualLeading.Length < prefix.Length)
            {
                return false;
            }

            for (var i = 0; i < prefix.Length; i++)
            {
                if (actualLeading[i] != prefix[i])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
```

`tests/BlurLink.Core.Tests/ReplyShapeValidatorTests.cs`: off-by-default true; length mismatch false; prefix mismatch false (flip one byte of a synthetic 160-byte reply with a scrubbed `10.0.0.x` address — synthetic payloads at real lengths only, never real capture hex); short-buffer false. (4 facts, exact bodies follow the table above line-for-line.)

Native: implement the identical rule where replies are classified (bridge + host-session reply paths), counting `replyShapeChecked` per reply evaluated and `replyShapeMismatch` per failure; never drop, never reinject differently — the original still flows. Add native `TestReplyShape` cases mirroring the managed four. Emit both counters in the status JSON; extend `SessionCounters` with `ReplyShapeChecked`/`ReplyShapeMismatch`, extend `SessionCounters.From`, and extend the `From_MapsEveryCounter` tripwire test with the two new fields (the task says why: new helper fields must not be silently dropped).

Diagnostics gets a "Reply shape" section: `"N checked, M mismatched (observe-only — never blocked)"`, bound to the two counters, hidden when both are zero. Interop (`scripts/test-interop.ps1`): add two contract checks — bridge `start` and host `start_host` carrying the shape fields parse and reach the clean driver-error path unelevated (same shape as the existing validation-path checks; name them `shape-fields-bridge-parses`, `shape-fields-host-parses`).

- [ ] **Step 4: Refusal reasons readable**

`src/BlurLink.Shell/ViewModels/RefusalExplainer.cs`:
```csharp
namespace BlurLink.Shell.ViewModels;

public static class RefusalExplainer
{
    public static string Explain(string code) => code switch
    {
        "dedup" => "re-captured echoes suppressed (loop backstop)",
        "rate" => "bursts refused by the rate limiter",
        "payload-gate" => "packets failing the payload prefix gate",
        "fragments" => "IP fragments reinjected, never cloned",
        "host-broadcast" => "broadcast-shaped replies refused (shape unverified)",
        "host-ambiguous" => "replies matching two players, refused not misdelivered",
        "host-unmatched" => "replies to unknown address/port",
        "host-collision" => "players refused on address+port collision",
        "announce-rejected" => "malformed introductions ignored",
        "injection-error" => "replies that could not be sent",
        _ => "unknown refusal",
    };
}
```
Copy matches the WPF/host copy and `docs/troubleshooting.md` wording — do not invent new explanations.

`tests/BlurLink.Core.Tests/RefusalExplainerTests.cs`:
```csharp
using BlurLink.Shell.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class RefusalExplainerTests
{
    [Theory]
    [InlineData("dedup")]
    [InlineData("rate")]
    [InlineData("payload-gate")]
    [InlineData("fragments")]
    [InlineData("host-broadcast")]
    [InlineData("host-ambiguous")]
    [InlineData("host-unmatched")]
    [InlineData("host-collision")]
    [InlineData("announce-rejected")]
    [InlineData("injection-error")]
    public void EveryKnownCode_ExplainsNonEmpty(string code)
        => Assert.NotEmpty(RefusalExplainer.Explain(code));

    [Fact]
    public void UnknownCode_SaysSo()
        => Assert.Equal("unknown refusal", RefusalExplainer.Explain("nope"));
}
```
(That is 10 theory cases + 1 fact = 11 new tests.)

`DiagnosticsViewModel` exposes `Refusals` (`IReadOnlyList<(string Text, long Count)>`, non-zero counters only, built from `SessionState` of both session VMs — it takes the two coordinators' states via a `Func<(SessionState Join, SessionState Host)>` supplied by MainViewModel); the view renders one row per refusal with count + explanation. All-zero states yield an empty list with the text "No refusals recorded."

- [ ] **Step 5: Run everything**

Run: `dotnet test BlurLink.sln -c Release --nologo`
Expected: 0 failed; total = previous 281 + 4 (ifIdx) + 4 (shape) + 11 (refusals: 10 theory + 1 fact) + headless shape-section test 1 = previous + 20 (tripwire extension lives inside an existing test, not new).

Native: `ctest --test-dir out/native-tests --output-on-failure` green. Interop: `pwsh scripts/test-interop.ps1 -HelperExe out/native/Release/blurlink-net.exe -WatchdogSec 4` green incl. the 2 new checks (47 total). Privacy + claims checkers green.

- [ ] **Step 6: Commit**

```bash
git add src/BlurLink.Core src/BlurLink.Contracts src/BlurLink.Net src/BlurLink.Shell tests scripts/test-e2e.ps1 scripts/test-interop.ps1
git status --short
git commit -m "Correctness extras: ifIdx filter terms, observe-only reply-shape counters, readable refusals"
```
No `out/`, `dist/`, binaries staged. Report names which e2e assertions await the next elevated run.

## Task 13: Accessibility floor + UI CI job + parity sign-off

**Files:**
- Modify: all six Shell views (names/watermarks/tab stops only — no layout changes)
- Create: `tests/BlurLink.Core.Tests/UiAudit.cs` (helper) + `tests/BlurLink.Core.Tests/MainWindowHeadlessTests.cs`
- Modify: `.github/workflows/ci.yml` (+ `ui` job)

**Interfaces:**
- Consumes: every prior task (all views exist).
- Produces: the accessibility floor met and enforced, headless coverage for every view, CI running it, signed-off parity.

- [ ] **Step 1: Write the audit that fails**

`tests/BlurLink.Core.Tests/UiAudit.cs`:
```csharp
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace BlurLink.Core.Tests;

public static class UiAudit
{
    /// <summary>Accessibility floor: every button named, every text input labelled,
    /// every interactive control tab-stoppable. Returns human-readable violations.</summary>
    public static IReadOnlyList<string> Audit(StyledElement root)
    {
        var violations = new List<string>();
        foreach (var el in root.GetVisualDescendants().OfType<Control>())
        {
            switch (el)
            {
                case Button b when string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)):
                    violations.Add($"Button without AutomationProperties.Name (content: '{(b.Content as string) ?? b.Content?.GetType().Name}')");
                    break;
                case TextBox t when string.IsNullOrWhiteSpace(AutomationProperties.GetName(t))
                    && string.IsNullOrWhiteSpace(t.Watermark):
                    violations.Add("TextBox without Name or Watermark");
                    break;
                case ComboBox c when string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)):
                    violations.Add("ComboBox without AutomationProperties.Name");
                    break;
            }

            if (el is Button or TextBox or ComboBox or ListBoxItem or MenuItem && !el.Focusable)
            {
                violations.Add($"{el.GetType().Name} is not focusable");
            }
        }

        return violations;
    }
}
```
(`Automation.AutomationProperties` is `Avalonia.Automation.AutomationProperties`; `GetVisualDescendants` is in `Avalonia.VisualTree`. Fix the usings to exactly those if the compiler complains, and record it.)

`tests/BlurLink.Core.Tests/MainWindowHeadlessTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void EveryView_PassesTheAccessibilityAudit()
    {
        using var app = TestShellApp.Create(); // helper below: real MainViewModel, verified profile set so no first-run overlay
        var violations = new List<string>();
        foreach (var view in new Avalonia.StyledElement[] { app.Join, app.Host, app.Settings, app.Diagnostics, app.FirstRun, app.Main })
        {
            violations.AddRange(UiAudit.Audit(view));
        }

        Assert.Empty(violations);
    }

    [AvaloniaFact]
    public void Navigation_ReachesAllFourDestinations()
    {
        using var app = TestShellApp.Create();

        foreach (var view in new[] { "Join", "Host", "Settings", "Diagnostics" })
        {
            app.Main.Navigate(view);
            Assert.Equal(view, app.Vm.CurrentView);
        }
    }
}
```

`TestShellApp` (in the same test file, exact shape — MainViewModel's real constructor is `(BlurLinkConfig config, NotificationCenter notices, IPlatformServices platform)` per Task 4):
```csharp
internal sealed class TestShellApp : IDisposable
{
    public Shell.ViewModels.MainViewModel Vm { get; }
    public JoinView Join { get; }
    public HostView Host { get; }
    public SettingsView Settings { get; }
    public DiagnosticsView Diagnostics { get; }
    public FirstRunView FirstRun { get; }
    public MainWindow Main { get; }

    private TestShellApp(Shell.ViewModels.MainViewModel vm)
    {
        Vm = vm;
        Join = new JoinView { DataContext = vm.Join };
        Host = new HostView { DataContext = vm.Host };
        Settings = new SettingsView { DataContext = vm.Settings };
        Diagnostics = new DiagnosticsView { DataContext = vm.Diagnostics };
        FirstRun = new FirstRunView { DataContext = vm.FirstRun };
        Main = new MainWindow { DataContext = vm };
        Main.Show();
    }

    public static TestShellApp Create()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.VerifiedProfileDate = "2026-09-14"; // hide the first-run overlay
        var vm = new Shell.ViewModels.MainViewModel(
            config,
            new BlurLink.Shell.Notifications.NotificationCenter(),
            new FakePlatformServices());
        return new TestShellApp(vm);
    }

    public void Dispose()
    {
        Main.Close();
        Vm.Dispose();
    }
}
```
Reuse `FakePlatformServices` from `SettingsHeadlessTests.cs` — do not redefine it. If Shell `MainViewModel` needs the config store/launcher itself (Task 4 may have kept the WPF shape with internally-created services), adapt the `Create()` body to the real constructor and record the deviation — the members above (`Vm.Join/Host/Settings/Diagnostics/FirstRun`, `MainWindow` with `Navigate(string)`) are the contract the tests bind.

Run: `dotnet test tests/BlurLink.Core.Tests --filter "MainWindowHeadlessTests" -c Release`
Expected: FAIL — missing names (the audit lists them) and possibly missing `Navigate` helper (add a `public void Navigate(string view)` method on the Shell `MainWindow` code-behind in this task that delegates to the VM — the tests need it, keyboard users benefit from one focus call after navigation: `MoveFocus` to the pane).

- [ ] **Step 2: Name everything, then watch it pass**

Add `AutomationProperties.Name` to every Button/ComboBox the audit names, `Watermark` (or Name) to every TextBox, across `JoinView`, `HostView`, `SettingsView`, `DiagnosticsView`, `FirstRunView`, `MainWindow`, `DialogWindow`. No layout, copy, or binding changes in this step — names only, so re-review is trivial. Re-run the audit test after each view; commit once green.

Keyboard checklist (manual, recorded in the commit message, not automated): Tab reaches every action on Join/Host/Settings/Diagnostics in visual order; Enter activates the focused button; Esc closes the dialog and dismisses first run; nav buttons show a visible focus ring (Fluent default — confirm, do not restyle).

- [ ] **Step 3: Add the UI CI job**

In `.github/workflows/ci.yml`, append:
```yaml
  ui:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - name: Headless UI tests
        run: dotnet test tests/BlurLink.Core.Tests -c Release --nologo --filter "FullyQualifiedName~HeadlessTests|FullyQualifiedName~Headless"
```
Rationale for the filter: it selects exactly the headless classes (`*HeadlessTests`, `DialogHeadlessTests`) without running the whole suite twice. Validate the YAML parses (open it, check indentation against the `docs` job) — the next push is the real parse check.

- [ ] **Step 4: Full verification + parity sign-off**

Run: `dotnet build BlurLink.sln -c Release --nologo` (0/0), `dotnet test BlurLink.sln -c Release --no-build --nologo` (0 failed; total = previous 301 + 2 MainWindow headless tests = 303), both docs checkers green, `scripts/test.ps1` green end to end.

Side-by-side parity run (WPF vs Shell, unelevated, recorded in the commit message): start/stop bridge, Detect discovery port, Listen for replies, host start + revoke, settings import/export, log viewer tail, first-run overlay + skip, support bundle creation. One line per item: same-or-better vs WPF, or the difference with a reason.

- [ ] **Step 5: Commit**

```bash
git add src/BlurLink.Shell tests/BlurLink.Core.Tests .github/workflows/ci.yml
git commit -m "Accessibility floor, headless coverage for every view, UI CI job; parity signed off"
```

---

## Verification ledger

Fill in as tasks complete (stage honest evidence, as the foundation plan did).

| Task | Status | Evidence to record |
|---|---|---|
| 1 | | sln builds Release 0/0; suite 246 green; Avalonia version in commit msg |
| 2 | | suite 246 green after the move; zero `Desktop.Services` hits outside moved files |
| 3 | | NotificationCenter + dialog tests green; suite 249; app compiles (no window yet — stated) |
| 4 | | Join headless 4/4; binding grep-compare clean; suite 253; unelevated launch sanity |
| 5 | | Host headless 2/2; suite 255; FormatStatus deletion named |
| 6 | | Settings+Diagnostics headless 4/4; suite 259; parity checklist in commit msg |
| 7 | | `docs/aot-spike.md` Avalonia row filled; staging/UAC verdict recorded |
| 8 | | FirstRun 5/5; suite 264; overlay + skip sanity (settings backup/restore noted) |
| 9 | | stability 4/4 + headless 1; suite 269; Q3 settled at 12 bytes |
| 10 | | bundle 3/3; suite 272; zip contents verified by entries |
| 11 | | notifier 9/9; suite 281; tray + tooltip sanity |
| 12 | | suite 301 (+20); native ctest green; interop 47 green; e2e assertions pending elevated run named |
| 13 | | audit green on all 7 views; ui job parses; side-by-side parity lines in commit msg |

Stage exit criteria: all 13 tasks done, full suite green, AOT verdict recorded, accessibility audit green, UI job in CI, parity signed off — WPF removal and release cut belong to the M4 plan.

## Execution handoff

**Two execution options:**

1. **Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — tasks executed in this session with checkpoints.

Which approach?





