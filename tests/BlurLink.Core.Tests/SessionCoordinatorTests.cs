using BlurLink.Contracts;
using BlurLink.Core.Session;
using Xunit;

namespace BlurLink.Core.Tests;

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

public sealed class SessionCoordinatorTests
{
    private static SessionCapabilities Ready =>
        new(HelperPresent: true, OverlayAddressKnown: true, AdapterSelected: true, GameRunning: true);

    [Fact]
    public async Task Refresh_ReportsRunningBridgeAndItsCounters()
    {
        var channel = new ScriptedChannel
        {
            DefaultStatus = """{"type":"status","active":true,"captured":4,"forwarded":4}""",
        };
        var sut = new SessionCoordinator(channel) { Capabilities = Ready };

        var state = await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);

        Assert.Equal(SessionMode.Bridge, state.Mode);
        Assert.Equal(SessionPhase.Running, state.Phase);
        Assert.Equal(4, state.Counters.Forwarded);
        Assert.Equal("bridge-forwarding", SessionStoryTable.Describe(state).Code);
    }

    [Fact]
    public async Task ARefusedStart_BecomesFailedWithTheHelperMessage()
    {
        var channel = new ScriptedChannel();
        channel.Enqueue("""{"type":"error","message":"another BlurLink bridge is already active on this PC"}""");
        var sut = new SessionCoordinator(channel);

        var state = await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);

        Assert.Equal(SessionPhase.Failed, state.Phase);
        Assert.Contains("already active", state.LastError);
    }

    [Fact]
    public async Task AnUnparseableStatus_BecomesFailed_NeverASilentSuccess()
    {
        var channel = new ScriptedChannel();
        var sut = new SessionCoordinator(channel);
        await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);
        channel.Enqueue("not json at all");

        var state = await sut.RefreshAsync(CancellationToken.None);

        Assert.Equal(SessionPhase.Failed, state.Phase);
        Assert.NotEmpty(state.LastError);
    }

    [Fact]
    public async Task WithoutAConnection_TheStateCarriesTheBlockingReason()
    {
        var channel = new ScriptedChannel { IsConnected = false };
        var sut = new SessionCoordinator(channel);

        var state = await sut.RefreshAsync(CancellationToken.None);

        Assert.Equal(SessionPhase.Idle, state.Phase);
        Assert.Contains(state.BlockingReasons, r => r.Code == "helper-not-running");
    }

    [Fact]
    public async Task Stop_AsksTheHelperAndReturnsToIdle()
    {
        var channel = new ScriptedChannel
        {
            DefaultStatus = """{"type":"status","active":true}""",
        };
        var sut = new SessionCoordinator(channel);
        await sut.StartAsync(new IpcStartRequest(), SessionMode.Bridge, CancellationToken.None);

        channel.DefaultStatus = """{"type":"status","active":false}""";
        var state = await sut.StopAsync(CancellationToken.None);

        Assert.Equal(SessionPhase.Idle, state.Phase);
        Assert.Equal(SessionMode.None, state.Mode);

        // start, the status poll that follows it, stop, then the status that confirms it.
        Assert.IsType<IpcStartRequest>(channel.Sent[0]);
        Assert.Equal(IpcMessageTypes.Stop, ((IpcSimpleCommand)channel.Sent[2]).Type);
        Assert.Equal(4, channel.Sent.Count);
    }
}
