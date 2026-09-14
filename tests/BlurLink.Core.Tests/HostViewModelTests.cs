using System.Net.NetworkInformation;
using BlurLink.Contracts;
using BlurLink.Core.Net;
using BlurLink.Platform;
using BlurLink.Desktop.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class HostViewModelTests
{
    private static HostViewModel Create(
        out FakeHelperProcess launcher,
        out BlurLinkConfig config,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>>? connect = null)
    {
        config = BlurLinkConfig.CreateDefault();
        launcher = new FakeHelperProcess("BlurLink-TESTPIPE", new string('A', 64));
        var capturedLauncher = launcher;
        var vm = new HostViewModel(
            config,
            () => { },
            capturedLauncher,
            connect ?? ((_, _, _) => throw new InvalidOperationException("no helper in this test")),
            () => { },
            _ => { });
        return vm;
    }

    private static AdapterInfo TestAdapter(int ifIndex = 7) => new(
        "Test Overlay", "test tap", ifIndex, OperationalStatus.Up,
        new[] { "25.1.2.3" }, null, null, null, true);

    // --- status framing -----------------------------------------------------

    [Fact]
    public void NoForwardsHearsThePrerequisiteNotTheFirewall()
    {
        using var vm = Create(out _, out _);
        vm.ApplyStatus(new IpcStatusResponse { HostActive = true, HostForwardsHeard = 0 });

        // The message must blame the overlay/forward path, not imply a firewall
        // or a closed lobby on the host.
        Assert.Contains("not receiving", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overlay", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firewall", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForwardsButNoReplies_PointsAtTheReplyPathNotTheOverlay()
    {
        using var vm = Create(out _, out _);
        vm.ApplyStatus(new IpcStatusResponse
        {
            HostActive = true,
            HostForwardsHeard = 5,
            HostRepliesForwarded = 0,
        });

        Assert.Contains("refresh Blur", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zero", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BroadcastReplies_AreExplainedRatherThanHidden()
    {
        using var vm = Create(out _, out _);
        vm.ApplyStatus(new IpcStatusResponse
        {
            HostActive = true,
            HostForwardsHeard = 3,
            HostRepliesForwarded = 1,
            HostBroadcastReplies = 2,
        });

        Assert.Contains("broadcast", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not verified", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not forwarded", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbiguousReplies_AreExplained()
    {
        using var vm = Create(out _, out _);
        vm.ApplyStatus(new IpcStatusResponse
        {
            HostActive = true,
            HostForwardsHeard = 4,
            HostRepliesForwarded = 2,
            HostAmbiguousReplies = 2,
        });

        Assert.Contains("two players", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wrong person", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnmatchedReplies_AreExplained()
    {
        using var vm = Create(out _, out _);
        vm.ApplyStatus(new IpcStatusResponse
        {
            HostActive = true,
            HostForwardsHeard = 4,
            HostRepliesForwarded = 0,
            HostUnmatchedReplies = 3,
        });

        Assert.Contains("not a known player", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostOff_SaysSo()
    {
        using var vm = Create(out _, out _);
        vm.ApplyStatus(new IpcStatusResponse { HostActive = false });
        Assert.Contains("off", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CountersAndPlayersLandOnTheViewModel()
    {
        using var vm = Create(out _, out _);
        vm.ApplyStatus(new IpcStatusResponse
        {
            HostActive = true,
            HostForwardsHeard = 9,
            HostRepliesForwarded = 4,
            // Both fields are set to different values on purpose: the Host tab
            // must show host mode's filter, never the bridge's. They are
            // mutually exclusive sessions, so reading the wrong one shows a
            // blank line in the real app and passes silently.
            Filter = "the-bridge-filter",
            HostFilter = "the-filter",
            HostPlayers =
            {
                new HostPlayerStatus
                {
                    OverlayIp = "25.1.2.3",
                    LanIp = "192.168.1.50",
                    BlurSourcePort = 51234,
                    ForwardsHeard = 9,
                    RepliesForwarded = 4,
                    InFilter = true,
                },
                new HostPlayerStatus
                {
                    OverlayIp = "25.4.5.6",
                    LanIp = "10.0.0.9",
                    BlurSourcePort = 40000,
                    ForwardsHeard = 3,
                    InFilter = false,
                },
            },
        });

        Assert.True(vm.HostRunning);
        Assert.Equal(9, vm.ForwardsHeard);
        Assert.Equal(4, vm.RepliesForwarded);
        Assert.Equal("the-filter", vm.ActiveFilter);
        Assert.Equal(2, vm.Players.Count);
        Assert.Contains("25.1.2.3", vm.Players[0].Summary);
        Assert.Contains("192.168.1.50:51234", vm.Players[0].Summary);
        Assert.Equal("active", vm.Players[0].State);

        // A quiet player stays listed (so its counters remain visible) but is
        // clearly marked.
        Assert.Equal("quiet", vm.Players[1].State);
    }

    [Fact]
    public void AQuietPlayerIsNotDroppedFromTheList()
    {
        using var vm = Create(out _, out _);

        // First status: one live player.
        vm.ApplyStatus(new IpcStatusResponse
        {
            HostActive = true,
            HostForwardsHeard = 3,
            HostPlayers = { new HostPlayerStatus { OverlayIp = "25.1.2.3", InFilter = true } },
        });
        Assert.Single(vm.Players);

        // Second: the same player has gone quiet. The row must survive, because
        // its forwards count is the diagnostic that explains the silence.
        vm.ApplyStatus(new IpcStatusResponse
        {
            HostActive = true,
            HostForwardsHeard = 3,
            HostPlayers = { new HostPlayerStatus { OverlayIp = "25.1.2.3", InFilter = false } },
        });
        Assert.Single(vm.Players);
        Assert.Equal("quiet", vm.Players[0].State);
    }

    // --- preflight and commands --------------------------------------------

    [Fact]
    public void PreflightRefusesBlurLinksOwnIntroductionPort()
    {
        using var vm = Create(out _, out _);
        vm.SelectedAdapter = TestAdapter();
        vm.DiscoveryPort = BlurLinkConstants.HostAnnounceUdpPort.ToString();

        Assert.Contains("introduction port", vm.Preflight, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreflightAsksForAPort()
    {
        using var vm = Create(out _, out _);
        vm.SelectedAdapter = TestAdapter();
        vm.DiscoveryPort = string.Empty;

        Assert.Contains("discovery port", vm.Preflight, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartWithoutAPort_RefusesLocallyWithoutTouchingTheHelper()
    {
        using var vm = Create(out var launcher, out _);
        vm.SelectedAdapter = TestAdapter();
        vm.DiscoveryPort = string.Empty;

        await vm.StartHostAsync();

        Assert.Equal(0, launcher.LaunchCount);
        Assert.Contains("discovery port", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HostRunning);
    }

    [Fact]
    public async Task Start_SendsStartHostWithThePortAndAdapter()
    {
        var pipeName = "BlurLink-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var config = BlurLinkConfig.CreateDefault();
        using var launcher = new FakeHelperProcess(pipeName, new string('A', 64));
        using var client = new HelperIpcClient();
        string? captured = null;
        using var server = new ScriptedPipeServer(pipeName, request =>
        {
            captured = request;
            return """{"type":"status","active":false,"hostActive":true,"hostForwardsHeard":2,"hostPlayers":[]}""";
        });

        using var vm = new HostViewModel(
            config, () => { }, launcher,
            async (alive, ct, timeout) => { await client.ConnectAsync(pipeName, ct, alive, timeout); return client; },
            () => { }, _ => { });
        vm.SelectedAdapter = TestAdapter(ifIndex: 11);
        vm.DiscoveryPort = "50001";

        await vm.StartHostAsync();

        Assert.NotNull(captured);
        Assert.Contains("\"type\":\"start_host\"", captured);
        Assert.Contains("\"discoveryUdpPort\":50001", captured);
        Assert.Contains("\"adapterIfIndex\":11", captured);
        Assert.True(vm.HostRunning);
    }

    [Fact]
    public async Task Start_ReportsAHelperRefusalWithoutPretendingItWorked()
    {
        var pipeName = "BlurLink-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var config = BlurLinkConfig.CreateDefault();
        using var launcher = new FakeHelperProcess(pipeName, new string('A', 64));
        using var client = new HelperIpcClient();
        using var server = new ScriptedPipeServer(pipeName, _ =>
            """{"type":"error","message":"WinDivertOpen failed: access denied. The helper must run elevated (UAC)."}""");

        using var vm = new HostViewModel(
            config, () => { }, launcher,
            async (alive, ct, timeout) => { await client.ConnectAsync(pipeName, ct, alive, timeout); return client; },
            () => { }, _ => { });
        vm.SelectedAdapter = TestAdapter();
        vm.DiscoveryPort = "50001";

        await vm.StartHostAsync();

        Assert.False(vm.HostRunning);
        Assert.Contains("access denied", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoAccept_IsPersistedToConfig()
    {
        using var vm = Create(out _, out var config);
        Assert.True(config.HostAutoAccept);

        vm.AutoAccept = false;
        Assert.False(config.HostAutoAccept);
    }
}
