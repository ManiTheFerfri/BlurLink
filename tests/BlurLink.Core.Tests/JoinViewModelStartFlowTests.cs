using BlurLink.Contracts;
using BlurLink.Platform;
using BlurLink.Desktop.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Regression for the primary flow: a successful Start must launch the helper
/// exactly once, never kill it, and leave the bridge running after the
/// automatic reply-listen. Previously StartReplyListenAsync re-entered
/// EnsureHelperAsync, which saw "helper running but session flag unset" and
/// force-restarted the helper it had just started — silently leaving the
/// bridge stopped while the UI still claimed "Bridge running".
/// </summary>
public sealed class JoinViewModelStartFlowTests
{
    private sealed class Harness : IDisposable
    {
        public FakeHelperProcess Launcher = null!;
        public HelperIpcClient Ipc = null!;
        public ScriptedPipeServer Server = null!;
        public JoinViewModel Vm = null!;
        public BlurLinkConfig Config = null!;
        public int ChangedCount;

        public void Dispose()
        {
            Vm.Dispose();
            Ipc.Dispose();
            Server.Dispose();
        }
    }

    private static Harness Create(
        string host = "100.96.47.177", int port = 50001, TimeSpan? helperExitDelay = null)
    {
        var pipeName = "BlurLink-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var h = new Harness
        {
            Launcher = new FakeHelperProcess(pipeName, new string('A', 64)),
            Ipc = new HelperIpcClient(),
        };
        h.Server = new ScriptedPipeServer(pipeName, request =>
        {
            // The real helper exits by itself on `shutdown`; model that so
            // StopAsync's wait-for-exit path does not burn its full timeout.
            if (request.Contains("\"shutdown\"", StringComparison.Ordinal))
            {
                if (helperExitDelay is { } delay)
                {
                    h.Launcher.SimulateExitAfter(delay);
                }
                else
                {
                    h.Launcher.SimulateExit();
                }
            }

            return "{\"type\":\"status\",\"active\":true,\"watchdogSec\":15}";
        });

        h.Config = BlurLinkConfig.CreateDefault();
        h.Config.HostOverlayIp = host;
        h.Config.DiscoveryUdpPort = port;

        h.Vm = new JoinViewModel(
            h.Config,
            () => h.ChangedCount++,
            h.Launcher,
            async (stillAlive, ct, timeout) =>
            {
                // Reuse an already-open session: a real reconnect needs a
                // second pipe instance, which this one-shot server does not have.
                if (!h.Ipc.Connected)
                {
                    await h.Ipc.ConnectAsync(pipeName, ct, stillAlive, timeout);
                }

                return h.Ipc;
            },
            () => { },
            _ => { });

        // A real adapter must be selected for validation to pass.
        h.Vm.SelectedAdapter = h.Vm.Adapters.FirstOrDefault();
        h.ChangedCount = 0;
        return h;
    }

    [Fact]
    public async Task SuccessfulStart_LaunchesOnce_NeverKills_AndKeepsBridgeRunning()
    {
        using var h = Create();

        await h.Vm.StartAsync();

        Assert.True(h.Vm.BridgeRunning);
        Assert.Equal(1, h.Launcher.LaunchCount);
        Assert.Equal(0, h.Launcher.KillCount);
        Assert.True(h.Launcher.IsRunning);
        // The automatic reply-listen must run on the same authenticated session.
        Assert.True(h.Vm.SniffRunning);
    }

    [Fact]
    public async Task StartAfterStop_StillLaunchesExactlyOncePerSession()
    {
        using var h = Create();

        await h.Vm.StartAsync();
        Assert.Equal(1, h.Launcher.LaunchCount);

        // StopAsync talks to the (fake) helper over the same pipe and then
        // kills it; that is the only legitimate Kill.
        await h.Vm.StopAsync();
        Assert.False(h.Vm.BridgeRunning);
        Assert.Equal(1, h.Launcher.KillCount);
    }

    [Fact]
    public async Task StopAsync_SeesASlowHelperExit_WithoutBurningTheWholeTimeout()
    {
        // The helper needs ~300 ms to leave after `shutdown`.
        using var h = Create(helperExitDelay: TimeSpan.FromMilliseconds(300));
        await h.Vm.StartAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Vm.StopAsync();
        sw.Stop();

        // The wait really observed the exit instead of assuming one...
        Assert.False(h.Launcher.IsRunning);
        Assert.Contains("exited cleanly", h.Vm.Message, StringComparison.Ordinal);
        // ...and it is bounded by the helper, not by the full 3 s timeout.
        Assert.InRange(sw.ElapsedMilliseconds, 250, 2900);
    }

    /// <summary>
    /// One-threaded-context stand-in for the WPF UI thread: continuations must
    /// run on its pump, so any blocking wait inside an awaited call shows up as
    /// time the pump spends stuck inside one callback.
    /// </summary>
    private sealed class PumpSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public long MaxCallbackMs { get; private set; }

        public long TotalCallbackMs { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void PumpUntil(Func<bool> done, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!done() && DateTime.UtcNow < deadline)
            {
                if (!_queue.TryTake(out var item, 25))
                {
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                item.Callback(item.State);
                sw.Stop();
                MaxCallbackMs = Math.Max(MaxCallbackMs, sw.ElapsedMilliseconds);
                TotalCallbackMs += sw.ElapsedMilliseconds;
            }
        }
    }

    [Fact]
    public void StopAsync_WaitsForTheHelperWithoutBlockingTheCallingThread()
    {
        var ctx = new PumpSynchronizationContext();
        var previous = SynchronizationContext.Current;
        long elapsedMs;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            using var h = Create(helperExitDelay: TimeSpan.FromMilliseconds(300));

            var start = h.Vm.StartAsync();
            ctx.PumpUntil(() => start.IsCompleted, 5000);
            Assert.True(start.IsCompleted, "StartAsync did not finish with a pumped context");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stop = h.Vm.StopAsync();
            ctx.PumpUntil(() => stop.IsCompleted, 5000);
            sw.Stop();

            Assert.True(stop.IsCompleted, "StopAsync did not finish with a pumped context");
            Assert.Contains("exited cleanly", h.Vm.Message, StringComparison.Ordinal);
            elapsedMs = sw.ElapsedMilliseconds;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // The wait really elapsed (the helper needed ~300 ms) ...
        Assert.InRange(elapsedMs, 250, 2900);
        // ... but it did not hold the UI thread: the old Thread.Sleep loop ran
        // inside a single continuation, so almost all of that 300 ms would have
        // been spent frozen inside one callback instead of idling between them.
        Assert.True(
            ctx.TotalCallbackMs * 4 < elapsedMs,
            $"the UI thread was busy for {ctx.TotalCallbackMs} ms of a {elapsedMs} ms wait " +
            $"(longest single callback {ctx.MaxCallbackMs} ms)");
    }

    [Fact]
    public void RefreshFromConfig_ReloadsFields_AndDoesNotReenterChangeNotification()
    {
        using var h = Create();
        h.Vm.HostIp = "10.0.0.1";
        h.Vm.DiscoveryPort = "1234";
        h.ChangedCount = 0;

        // Simulate Settings Import/Reset mutating the shared config directly.
        h.Config.HostOverlayIp = "203.0.113.7";
        h.Config.DiscoveryUdpPort = 60001;
        h.Config.PayloadPrefixHex = "AA BB";
        h.Config.BroadcastDestination = "255.255.255.255";

        h.Vm.RefreshFromConfig();

        Assert.Equal("203.0.113.7", h.Vm.HostIp);
        Assert.Equal("60001", h.Vm.DiscoveryPort);
        Assert.Equal("AA BB", h.Vm.PayloadHex);
        // Reloading must not fire a save cycle or write the old UI value back.
        Assert.Equal(0, h.ChangedCount);
        Assert.Equal("203.0.113.7", h.Config.HostOverlayIp);
    }
}
