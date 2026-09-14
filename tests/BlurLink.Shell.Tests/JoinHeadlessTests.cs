using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Core.Session;
using BlurLink.Shell.Notifications;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
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
}
