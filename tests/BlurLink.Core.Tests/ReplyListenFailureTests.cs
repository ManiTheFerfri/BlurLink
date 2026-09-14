using BlurLink.Contracts;
using BlurLink.Desktop.Services;
using BlurLink.Desktop.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Regression: the automatic reply-listen is observe-only and optional, and it
/// rides the bridge session StartAsync just authenticated. When the helper
/// refused that sniff, SendSniffRequestAsync killed the helper and dropped the
/// connection while BridgeRunning stayed true — the GUI kept claiming a live
/// bridge on top of a dead helper, all because of a failed optional listener.
/// A refused sniff that owns nothing (discovery listening) must still clean up
/// the stray elevated helper it launched.
/// </summary>
public sealed class ReplyListenFailureTests
{
    private sealed class Harness : IDisposable
    {
        public FakeHelperProcess Launcher = null!;
        public HelperIpcClient Ipc = null!;
        public ScriptedPipeServer Server = null!;
        public JoinViewModel Vm = null!;
        public BlurLinkConfig Config = null!;

        public void Dispose()
        {
            Vm.Dispose();
            Ipc.Dispose();
            Server.Dispose();
        }
    }

    private static Harness Create(bool refuseSniff)
    {
        var pipeName = "BlurLink-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var h = new Harness
        {
            Launcher = new FakeHelperProcess(pipeName, new string('A', 64)),
            Ipc = new HelperIpcClient(),
        };
        h.Server = new ScriptedPipeServer(pipeName, request =>
        {
            if (refuseSniff && request.Contains("\"type\":\"sniff\"", StringComparison.Ordinal))
            {
                // What the helper returns when the second (observe-only) handle
                // cannot be opened while the bridge already holds one.
                return "{\"type\":\"error\",\"message\":\"listen refused by the driver\"}";
            }

            if (request.Contains("\"shutdown\"", StringComparison.Ordinal))
            {
                h.Launcher.SimulateExit();
            }

            return "{\"type\":\"status\",\"active\":true,\"watchdogSec\":15}";
        });

        h.Config = BlurLinkConfig.CreateDefault();
        h.Config.HostOverlayIp = "100.96.47.177";
        h.Config.DiscoveryUdpPort = 50001;

        h.Vm = new JoinViewModel(
            h.Config,
            () => { },
            h.Launcher,
            async (stillAlive, ct, timeout) =>
            {
                if (!h.Ipc.Connected)
                {
                    await h.Ipc.ConnectAsync(pipeName, ct, stillAlive, timeout);
                }

                return h.Ipc;
            },
            () => { },
            _ => { });

        h.Vm.SelectedAdapter = h.Vm.Adapters.FirstOrDefault();
        return h;
    }

    [Fact]
    public async Task AcceptedAutoReplyListen_StartsTheListenerNextToTheBridge()
    {
        using var h = Create(refuseSniff: false);

        await h.Vm.StartAsync();

        Assert.True(h.Vm.BridgeRunning);
        Assert.True(h.Vm.SniffRunning);
        Assert.Equal(0, h.Launcher.KillCount);
    }

    [Fact]
    public async Task RefusedAutoReplyListen_LeavesTheLiveBridgeAlone()
    {
        using var h = Create(refuseSniff: true);

        await h.Vm.StartAsync();

        // The bridge survives a refused optional listener...
        Assert.True(h.Vm.BridgeRunning);
        Assert.True(h.Launcher.IsRunning);
        Assert.Equal(0, h.Launcher.KillCount);
        Assert.Equal("Stop Bridge", h.Vm.StartStopText);
        // ...the listener itself did not start, and it says why.
        Assert.False(h.Vm.SniffRunning);
        Assert.Contains("refused", h.Vm.RepliesSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusedDiscoveryListen_StillTearsDownItsOwnStrayHelper()
    {
        // Nothing is bridged here, so the helper existed only for the listen:
        // refusing it must not leave an elevated process behind.
        using var h = Create(refuseSniff: true);

        await h.Vm.SniffAsync(replies: false);

        Assert.Equal(1, h.Launcher.LaunchCount);
        Assert.Equal(1, h.Launcher.KillCount);
        Assert.False(h.Launcher.IsRunning);
        Assert.False(h.Vm.BridgeRunning);
        Assert.False(h.Vm.SniffRunning);
        Assert.Contains("rejected", h.Vm.SniffStatus, StringComparison.OrdinalIgnoreCase);
    }
}
