using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Text;
using BlurLink.Contracts;
using BlurLink.Core.Config;
using BlurLink.Core.Net;
using BlurLink.Platform;
using BlurLink.Shell.Notifications;
using BlurLink.Shell.ViewModels;
using Xunit;

namespace BlurLink.Shell.Tests;

// Ports of the behavior tests deleted with the WPF VMs (M4 Task 4), retrieved
// from git (git show 495c26d:tests/BlurLink.Core.Tests/<File>.cs) and pointed
// at the Shell counterparts, which claim verbatim ports. Bodies are unchanged
// except the Shell routing (Join tab split: Session = JoinSessionViewModel,
// Settings = BridgeSettingsViewModel, Sniff = DiscoverySniffViewModel) and the
// adaptations noted per class. All pure VM logic: v3 [Fact], no views.
//
// Ported here (31): Host 9 of 14, Join story 2, Join busy-state 5, Join start
// flow 5, reply-listen 3, Settings import/reset 5, Main construction 2.
// Reasoned non-ports (5, see report): the Host FormatStatus sentence tests —
// Shell retired FormatStatus by design (StatusText keeps the last action; the
// story banner is the sentence) and SessionStoryTests covers the replacement.

/// <summary>
/// In-memory stand-in for the elevated helper process. Local mirror of the
/// Core.Tests double (internal there; Shell.Tests must not reference that
/// project — the two test projects target different xunit majors).
/// </summary>
internal sealed class FakeHelperProcess : IHelperProcess
{
    public FakeHelperProcess(string pipeName, string token)
    {
        PipeName = pipeName;
        Token = token;
    }

    public string PipeName { get; }
    public string Token { get; }
    public bool IsRunning { get; private set; }
    public int LaunchCount { get; private set; }
    public int KillCount { get; private set; }

    public void Launch(string? helperPath = null, int watchdogSec = 15, string? logFilePath = null)
    {
        LaunchCount++;
        IsRunning = true;
    }

    public void Kill()
    {
        KillCount++;
        IsRunning = false;
    }

    /// <summary>Simulates the helper exiting on its own (e.g. after shutdown).</summary>
    public void SimulateExit() => IsRunning = false;

    /// <summary>
    /// Simulates a helper that takes a while to shut down (drains WinDivert,
    /// flushes its log), so the GUI's wait-for-exit path is exercised.
    /// </summary>
    public void SimulateExitAfter(TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            IsRunning = false;
        });
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// One-connection JSON-lines pipe server that answers every request with a
/// canned response, so the real <see cref="HelperIpcClient"/> framing is
/// exercised without any helper binary or elevation. Local mirror of the
/// Core.Tests double (see <see cref="FakeHelperProcess"/> for why).
/// </summary>
internal sealed class ScriptedPipeServer : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly Func<string, string> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public ScriptedPipeServer(string name, Func<string, string> respond)
    {
        _respond = respond;
        _server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await _server.WaitForConnectionAsync(_cts.Token);
            using var reader = new StreamReader(_server, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(_server, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            while (!_cts.IsCancellationRequested && _server.IsConnected)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    break;
                }

                await writer.WriteLineAsync(_respond(line));
            }
        }
        catch
        {
            // best effort: the test owns teardown
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Dispose();
        _cts.Dispose();
    }
}

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

public sealed class JoinStoryWiringTests
{
    [Fact]
    public void TheJoinTabShowsTheStoryNotRawCounters()
    {
        // Same seam the existing view-model tests use: a fake helper channel.
        var vm = JoinViewModel.ForTests(new ScriptedChannel());
        vm.ApplyStatusForTests(new BlurLink.Contracts.IpcStatusResponse
        {
            Active = true, Captured = 4, Forwarded = 4,
        });

        Assert.Equal("You are reaching the host", vm.Story.Headline);
        Assert.Contains("captured=4", vm.Session.RawCounters);
    }

    /// <summary>
    /// Sanity-check evidence (headless: no screenshot possible): a freshly
    /// constructed production tab with no helper running reports the blocked
    /// story rather than a stale counter line, and the Counters disclosure
    /// shows the raw numbers.
    /// </summary>
    [Fact]
    public void FreshTabWithNoHelper_ShowsTheBlockedStoryNotACounterLine()
    {
        var launcher = new FakeHelperProcess("BlurLink-TEST", new string('A', 64));
        using var vm = new JoinViewModel(
            BlurLinkConfig.CreateDefault(),
            () => { },
            launcher,
            (_, _, _) => { throw new InvalidOperationException("no pipe in this test"); },
            () => { },
            _ => { });

        Assert.Equal("blocked", vm.Story.Code);
        Assert.Contains("captured=0", vm.Session.RawCounters);
    }
}

/// <summary>
/// Regression tests for the Join busy-state machine. Shell routing: fields
/// live on the Settings part, Start/Stop on the Session part, Detect/Listen
/// on the Sniff part. One deliberate adaptation: Start enablement in Shell is
/// preflight-driven by design (disabled for bad input), so the "commands must
/// remain enabled" proof for Start is a second StartAsync still running
/// validation (new input produces a new message) instead of returning early
/// on a latched busy flag. Detect/Listen enablement asserts are verbatim.
/// </summary>
public sealed class JoinViewModelBusyStateTests : IDisposable
{
    private readonly MainViewModel _vm;

    public JoinViewModelBusyStateTests()
    {
        _vm = new MainViewModel(
            BlurLinkConfig.CreateDefault(), new NotificationCenter(), new FakePlatformServices());
    }

    public void Dispose()
    {
        _vm.Dispose();
    }

    [Fact]
    public async Task StartAsync_WithInvalidInput_ReleasesBusy()
    {
        // No host IP / port entered: validation must fail.
        _vm.Join.Settings.HostIp = "not-an-ip";
        await _vm.Join.Session.StartAsync();
        var first = _vm.Join.Session.Message;
        Assert.False(_vm.Join.Session.BridgeRunning);
        Assert.False(string.IsNullOrEmpty(first));
        // The commands must remain enabled (busy flag released).
        Assert.True(_vm.Join.Sniff.DetectCommand.CanExecute(null));
        // Shell proof the flag is not latched: differently-invalid input on a
        // second StartAsync produces the new validation message, not an early
        // return with the stale one.
        _vm.Join.Settings.HostIp = "100.96.47.177";
        _vm.Join.Settings.DiscoveryPort = "70000"; // out of range
        await _vm.Join.Session.StartAsync();
        Assert.False(_vm.Join.Session.BridgeRunning);
        Assert.NotEqual(first, _vm.Join.Session.Message);
        Assert.Contains("port", _vm.Join.Session.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_WithInvalidPort_ReleasesBusy()
    {
        _vm.Join.Settings.HostIp = "100.96.47.177";
        _vm.Join.Settings.DiscoveryPort = "70000"; // out of range
        await _vm.Join.Session.StartAsync();
        var first = _vm.Join.Session.Message;
        Assert.False(_vm.Join.Session.BridgeRunning);
        Assert.True(_vm.Join.Sniff.ListenRepliesCommand.CanExecute(null));
        // Shell proof the flag is not latched (see above test).
        _vm.Join.Settings.HostIp = "not-an-ip";
        await _vm.Join.Session.StartAsync();
        Assert.False(_vm.Join.Session.BridgeRunning);
        Assert.NotEqual(first, _vm.Join.Session.Message);
    }

    [Fact]
    public async Task StartAsync_TwiceAfterFailure_StillValidates()
    {
        _vm.Join.Settings.HostIp = "not-an-ip";
        await _vm.Join.Session.StartAsync();
        _vm.Join.Settings.HostIp = "also-bad";
        await _vm.Join.Session.StartAsync();
        // Still responsive: the busy flag must not be latched. The second run
        // re-validated (message present) instead of returning early; a third
        // run with differently-invalid input produces the new message.
        Assert.False(_vm.Join.Session.BridgeRunning);
        Assert.False(string.IsNullOrEmpty(_vm.Join.Session.Message));
        _vm.Join.Settings.HostIp = "100.96.47.177";
        _vm.Join.Settings.DiscoveryPort = "70000"; // out of range
        await _vm.Join.Session.StartAsync();
        Assert.False(_vm.Join.Session.BridgeRunning);
        Assert.Contains("port", _vm.Join.Session.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SniffAsync_RepliesWithoutPort_ReleasesBusyAndNeverLaunchesHelper()
    {
        // Force the missing-port validation path so no helper is ever
        // launched (tests must stay hermetic; this machine may even have
        // the WinDivert driver installed).
        _vm.Join.Settings.DiscoveryPort = string.Empty;
        await _vm.Join.Sniff.SniffAsync(replies: true);
        Assert.Contains("Set the discovery port first", _vm.Join.Sniff.SniffStatus);
        Assert.False(_vm.Join.Sniff.SniffRunning);
        Assert.True(_vm.Join.Sniff.DetectCommand.CanExecute(null));
    }

    [Fact]
    public void StartStopText_ReflectsState()
    {
        Assert.Equal("Start Bridge", _vm.Join.Session.StartStopText);
        Assert.True(_vm.Join.Session.ShowStart);
        Assert.False(_vm.Join.Session.ShowForceKill);
    }
}

/// <summary>
/// Regression for the primary flow (Shell routing: Start/Stop on the Session
/// part, fields on the Settings part, auto reply-listen on the Sniff part).
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
        h.Vm.Settings.SelectedAdapter = h.Vm.Settings.Adapters.FirstOrDefault();
        h.ChangedCount = 0;
        return h;
    }

    [Fact]
    public async Task SuccessfulStart_LaunchesOnce_NeverKills_AndKeepsBridgeRunning()
    {
        using var h = Create();

        await h.Vm.Session.StartAsync();

        Assert.True(h.Vm.Session.BridgeRunning);
        Assert.Equal(1, h.Launcher.LaunchCount);
        Assert.Equal(0, h.Launcher.KillCount);
        Assert.True(h.Launcher.IsRunning);
        // The automatic reply-listen must run on the same authenticated session.
        Assert.True(h.Vm.Sniff.SniffRunning);
    }

    [Fact]
    public async Task StartAfterStop_StillLaunchesExactlyOncePerSession()
    {
        using var h = Create();

        await h.Vm.Session.StartAsync();
        Assert.Equal(1, h.Launcher.LaunchCount);

        // StopAsync talks to the (fake) helper over the same pipe and then
        // kills it; that is the only legitimate Kill.
        await h.Vm.Session.StopAsync();
        Assert.False(h.Vm.Session.BridgeRunning);
        Assert.Equal(1, h.Launcher.KillCount);
    }

    [Fact]
    public async Task StopAsync_SeesASlowHelperExit_WithoutBurningTheWholeTimeout()
    {
        // The helper needs ~300 ms to leave after `shutdown`.
        using var h = Create(helperExitDelay: TimeSpan.FromMilliseconds(300));
        await h.Vm.Session.StartAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Vm.Session.StopAsync();
        sw.Stop();

        // The wait really observed the exit instead of assuming one...
        Assert.False(h.Launcher.IsRunning);
        Assert.Contains("exited cleanly", h.Vm.Session.Message, StringComparison.Ordinal);
        // ...and it is bounded by the helper, not by the full 3 s timeout.
        Assert.InRange(sw.ElapsedMilliseconds, 250, 2900);
    }

    /// <summary>
    /// One-threaded-context stand-in for the UI thread: continuations must
    /// run on its pump, so any blocking wait inside an awaited call shows up as
    /// time the pump spends stuck inside one callback.
    /// </summary>
    private sealed class PumpSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

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

            var start = h.Vm.Session.StartAsync();
            ctx.PumpUntil(() => start.IsCompleted, 5000);
            Assert.True(start.IsCompleted, "StartAsync did not finish with a pumped context");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stop = h.Vm.Session.StopAsync();
            ctx.PumpUntil(() => stop.IsCompleted, 5000);
            sw.Stop();

            Assert.True(stop.IsCompleted, "StopAsync did not finish with a pumped context");
            Assert.Contains("exited cleanly", h.Vm.Session.Message, StringComparison.Ordinal);
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
        h.Vm.Settings.HostIp = "10.0.0.1";
        h.Vm.Settings.DiscoveryPort = "1234";
        h.ChangedCount = 0;

        // Simulate Settings Import/Reset mutating the shared config directly.
        h.Config.HostOverlayIp = "203.0.113.7";
        h.Config.DiscoveryUdpPort = 60001;
        h.Config.PayloadPrefixHex = "AA BB";
        h.Config.BroadcastDestination = "255.255.255.255";

        h.Vm.RefreshFromConfig();

        Assert.Equal("203.0.113.7", h.Vm.Settings.HostIp);
        Assert.Equal("60001", h.Vm.Settings.DiscoveryPort);
        Assert.Equal("AA BB", h.Vm.Settings.PayloadHex);
        // Reloading must not fire a save cycle or write the old UI value back.
        Assert.Equal(0, h.ChangedCount);
        Assert.Equal("203.0.113.7", h.Config.HostOverlayIp);
    }
}

/// <summary>
/// Regression: the automatic reply-listen is observe-only and optional (Shell
/// routing: Start on the Session part, listen state on the Sniff part).
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

        h.Vm.Settings.SelectedAdapter = h.Vm.Settings.Adapters.FirstOrDefault();
        return h;
    }

    [Fact]
    public async Task AcceptedAutoReplyListen_StartsTheListenerNextToTheBridge()
    {
        using var h = Create(refuseSniff: false);

        await h.Vm.Session.StartAsync();

        Assert.True(h.Vm.Session.BridgeRunning);
        Assert.True(h.Vm.Sniff.SniffRunning);
        Assert.Equal(0, h.Launcher.KillCount);
    }

    [Fact]
    public async Task RefusedAutoReplyListen_LeavesTheLiveBridgeAlone()
    {
        using var h = Create(refuseSniff: true);

        await h.Vm.Session.StartAsync();

        // The bridge survives a refused optional listener...
        Assert.True(h.Vm.Session.BridgeRunning);
        Assert.True(h.Launcher.IsRunning);
        Assert.Equal(0, h.Launcher.KillCount);
        Assert.Equal("Stop Bridge", h.Vm.Session.StartStopText);
        // ...the listener itself did not start, and it says why.
        Assert.False(h.Vm.Sniff.SniffRunning);
        Assert.Contains("refused", h.Vm.Sniff.RepliesSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusedDiscoveryListen_StillTearsDownItsOwnStrayHelper()
    {
        // Nothing is bridged here, so the helper existed only for the listen:
        // refusing it must not leave an elevated process behind.
        using var h = Create(refuseSniff: true);

        await h.Vm.Sniff.SniffAsync(replies: false);

        Assert.Equal(1, h.Launcher.LaunchCount);
        Assert.Equal(1, h.Launcher.KillCount);
        Assert.False(h.Launcher.IsRunning);
        Assert.False(h.Vm.Session.BridgeRunning);
        Assert.False(h.Vm.Sniff.SniffRunning);
        Assert.Contains("rejected", h.Vm.Sniff.SniffStatus, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Regression: Settings Import/Reset wrote the shared config but the Join tab
/// kept its old values, so the next Start silently overwrote the imported
/// values back to the stale ones. Shell wiring mirrors the original: the
/// Settings part's onChanged callback is the Join tab's RefreshFromConfig, so
/// Import/Reset reload the fields. Adaptation: Join fields live on the
/// Settings part of the facade, and Shell SettingsViewModel has no Dispose.
/// </summary>
public sealed class SettingsImportResetTests
{
    private static (JoinViewModel Join, SettingsViewModel Settings) Build(
        BlurLinkConfig config, Action<string>? applyLogLevel = null)
    {
        var join = new JoinViewModel(
            config,
            () => { },
            new FakeHelperProcess("BlurLink-0123456789ABCDEF", new string('A', 64)),
            (_, _, _) => throw new InvalidOperationException("helper must not launch in this test"),
            () => { },
            _ => { });
        var settings = new SettingsViewModel(config, () => join.RefreshFromConfig(), applyLogLevel);
        return (join, settings);
    }

    [Fact]
    public void Import_RefreshesJoinFields_SoStartCannotClobberImportedValues()
    {
        var config = BlurLinkConfig.CreateDefault();
        var (join, settings) = Build(config);
        try
        {
            join.Settings.HostIp = "10.0.0.1";
            join.Settings.DiscoveryPort = "1234";

            settings.ExportText = BlurLinkConfigStore.ExportJson(new BlurLinkConfig
            {
                HostOverlayIp = "100.96.47.177",
                DiscoveryUdpPort = 50001,
                BroadcastDestination = "255.255.255.255",
                PayloadPrefixHex = "42 4C",
            });
            settings.ImportCommand.Execute(null);

            Assert.Equal("100.96.47.177", join.Settings.HostIp);
            Assert.Equal("50001", join.Settings.DiscoveryPort);
            Assert.Equal("42 4C", join.Settings.PayloadHex);
            // Config and Join tab now agree, so Start persists the imported values.
            Assert.Equal("100.96.47.177", config.HostOverlayIp);
            Assert.Equal(50001, config.DiscoveryUdpPort);
        }
        finally
        {
            join.Dispose();
        }
    }

    [Fact]
    public void Import_PushesTheImportedLogLevelIntoTheLiveLogger()
    {
        var config = BlurLinkConfig.CreateDefault();
        var applied = new List<string>();
        var (join, settings) = Build(config, applied.Add);
        try
        {
            settings.ExportText = BlurLinkConfigStore.ExportJson(
                new BlurLinkConfig { LogLevel = "Error" });
            settings.ImportCommand.Execute(null);

            // The imported level must reach the logger, not just settings.json:
            // otherwise the file says Error while Debug chatter keeps flowing.
            Assert.Equal("Error", config.LogLevel);
            Assert.Equal(new[] { "Error" }, applied);
        }
        finally
        {
            join.Dispose();
        }
    }

    [Fact]
    public void Reset_PushesTheDefaultLogLevelIntoTheLiveLogger()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.LogLevel = "Debug";
        var applied = new List<string>();
        var (join, settings) = Build(config, applied.Add);
        try
        {
            settings.ResetCommand.Execute(null);

            Assert.Equal(BlurLinkConstants.DefaultLogLevel, config.LogLevel);
            Assert.Equal(new[] { BlurLinkConstants.DefaultLogLevel }, applied);
        }
        finally
        {
            join.Dispose();
        }
    }

    [Fact]
    public void Save_StillPushesTheChosenLogLevelIntoTheLiveLogger()
    {
        var config = BlurLinkConfig.CreateDefault();
        var applied = new List<string>();
        var (join, settings) = Build(config, applied.Add);
        try
        {
            settings.LogLevel = "Warning";
            settings.SaveCommand.Execute(null);

            Assert.Equal("Warning", config.LogLevel);
            Assert.Equal(new[] { "Warning" }, applied);
        }
        finally
        {
            join.Dispose();
        }
    }

    [Fact]
    public void Reset_RestoresFixedPortAndClearsOtherResearchValues()
    {
        // Task A (renamed from Reset_ClearsResearchValuesInConfigAndOnTheJoinTab):
        // Reset restores the fixed 50001 default instead of an empty port, while
        // host IP / payload hex are still cleared.
        var config = BlurLinkConfig.CreateDefault();
        config.HostOverlayIp = "100.96.47.177";
        config.DiscoveryUdpPort = 50001;
        config.PayloadPrefixHex = "42 4C";
        var (join, settings) = Build(config);
        try
        {
            Assert.Equal("100.96.47.177", join.Settings.HostIp);

            settings.ResetCommand.Execute(null);

            Assert.Equal(string.Empty, join.Settings.HostIp);
            Assert.Equal("50001", join.Settings.DiscoveryPort);
            Assert.Equal(string.Empty, join.Settings.PayloadHex);
            Assert.Equal(string.Empty, config.HostOverlayIp);
            Assert.Equal(50001, config.DiscoveryUdpPort);
            Assert.Equal(string.Empty, config.PayloadPrefixHex);
        }
        finally
        {
            join.Dispose();
        }
    }
}

/// <summary>
/// Regression: constructing the main view-model must never fault, even
/// though adapter selection fires change notifications mid-construction
/// (previously: reentrant OnConfigChanged hit unassigned VMs — adapter
/// enumeration reported failure on a healthy machine). Shell adaptation: the
/// composed ctor (config, notices, platform) with the shared test fake; Join
/// fields live on the Settings/Session parts.
/// </summary>
public sealed class MainViewModelConstructionTests
{
    private static MainViewModel Create() => new(
        BlurLinkConfig.CreateDefault(), new NotificationCenter(), new FakePlatformServices());

    [Fact]
    public void ConstructsCleanly_WithAdaptersLoaded()
    {
        using var vm = Create();
        Assert.NotNull(vm.Join.Settings.SelectedAdapter);
        Assert.Equal(string.Empty, vm.Join.Session.Message);
        Assert.Equal(7, vm.Join.Settings.PreflightItems.Count);
        Assert.Contains(vm.Join.Settings.PreflightItems, i => i.Label == "Overlay adapter" && i.Ok);
    }

    [Fact]
    public void Navigate_RefreshesAdapters_WithoutThrowing()
    {
        using var vm = Create();
        vm.CurrentView = "Settings";
        vm.RefreshAllAdapters();
        Assert.NotEmpty(vm.Join.Settings.Adapters);
    }
}
