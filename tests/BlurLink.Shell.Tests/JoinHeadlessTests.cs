using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Core.Session;
using BlurLink.Shell.Notifications;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using System.Diagnostics;
using Xunit;

namespace BlurLink.Shell.Tests;

/// <summary>
/// Local mirror of the Core.Tests ScriptedChannel: that type is internal to
/// the Core.Tests assembly (which Shell.Tests must not reference — the two
/// test projects target different xunit majors), so it is redefined here
/// rather than made visible to a second assembly.
/// </summary>
internal sealed class ScriptedChannel : IHelperChannel
{
    private readonly Queue<string> _responses = new();

    /// <summary>
    /// Answer used once the scripted queue is empty. This is the helper's steady
    /// state — {"active":false} by default, so a test that forgets to script a
    /// response gets"no session" rather than a misleading success.
    /// </summary>
    public string DefaultStatus { get; set; } = """{"type":"status","active":false}""";

    public List<object> Sent { get; } = new();
    public bool IsConnected { get; set; } = true;

    public void Enqueue(string json) => _responses.Enqueue(json);

    public Task<string> SendAsync(object message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : DefaultStatus);
    }
}

public sealed class JoinHeadlessTests
{
    private static JoinViewModel ForBridge() => JoinViewModel.ForTests(new ScriptedChannel());

    [AvaloniaFact]
    public void ForwardingStory_RendersTheHeadline()
    {
        var vm = ForBridge();
        vm.ApplyStatusForTests(new IpcStatusResponse { Active = true, Captured = 4, Forwarded = 4 });
        var view = new JoinView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var headline = view.FindControl<TextBlock>("StoryHeadline");
            Assert.NotNull(headline);
            Assert.Equal("You are reaching the host", headline.Text);
            Assert.Equal("bridge-forwarding", vm.Story.Code);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Start_IsDisabled_UntilPreflightPasses()
    {
        var vm = ForBridge();
        var view = new JoinView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var start = view.FindControl<Button>("StartStopButton");
            Assert.NotNull(start);
            Assert.False(vm.PreflightReady);
            // Avalonia gates command enablement through IsEnabledCore (surfaced
            // as IsEffectivelyEnabled); IsEnabled itself never reflects
            // CanExecute, so the brief's WPF-shaped IsEnabled assert becomes this.
            Assert.False(start.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
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
        var center = new NotificationCenter();
        center.Notify("t", "m", NoticeSeverity.Info);
        var notice = Assert.Single(center.Notices);

        center.DismissCommand.Execute(notice);

        Assert.Empty(center.Notices);
    }

    [Fact]
    public void Launch_UsesGameFolderAsWorkdir()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.BlurExePath = @"C:\Games\Blur\Blur.exe";
        config.BlurArgs = "--windowed --port 50001";

        var psi = JoinSessionViewModel.BuildBlurStartInfo(config);

        Assert.Equal(@"C:\Games\Blur\Blur.exe", psi.FileName);
        Assert.Equal(@"C:\Games\Blur", psi.WorkingDirectory);
        Assert.Equal("--windowed --port 50001", psi.Arguments);
    }

    [Fact]
    public async Task BlurExit_AutoStops_WhenEnabled()
    {
        // The watcher marshals through the ambient context when there is one;
        // null it so the exit lands inline and the test cannot deadlock.
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var vm = ForBridge();
            var session = vm.Session;
            session.BridgeRunning = true;
            session.StopWhenBlurExits = true;

            using var sleeper = StartExitingProcess(42);
            session.WatchProcessForTests(sleeper);

            Assert.True(await WaitForAsync(() => !session.BridgeRunning, TimeSpan.FromSeconds(20)));
            Assert.Equal("Bridge stopped — helper exited cleanly. No interception remains.", session.Message);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    [Fact]
    public async Task BlurExit_KeepsBridge_WhenOptedOut()
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var vm = ForBridge();
            var session = vm.Session;
            session.BridgeRunning = true;
            session.StopWhenBlurExits = false;

            using var sleeper = StartExitingProcess(42);
            session.WatchProcessForTests(sleeper);

            Assert.True(await WaitForAsync(
                () => session.Message.Contains("(code 42)", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20)));
            Assert.True(session.BridgeRunning);
            Assert.StartsWith("Blur exited", session.Message, StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    private static Process StartExitingProcess(int code)
    {
        var proc = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds 1000; exit {code}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(proc);
        return proc;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return condition();
    }
}
