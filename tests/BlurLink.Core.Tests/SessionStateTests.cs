using BlurLink.Contracts;
using BlurLink.Core.Session;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class SessionStateTests
{
    [Fact]
    public void From_MapsEveryCounter_SoANewCounterCannotBeSilentlyDropped()
    {
        var status = new IpcStatusResponse
        {
            Captured = 1, Forwarded = 2, Reinjected = 3, Dropped = 4, DedupSkipped = 5,
            AnnouncementsSent = 6, HostForwardsHeard = 7, HostRepliesForwarded = 8,
            HostBroadcastReplies = 9, HostAmbiguousReplies = 10,
        };

        var counters = SessionCounters.From(status);

        Assert.Equal(1, counters.Captured);
        Assert.Equal(2, counters.Forwarded);
        Assert.Equal(3, counters.Reinjected);
        Assert.Equal(4, counters.Dropped);
        Assert.Equal(5, counters.DedupSkipped);
        Assert.Equal(6, counters.AnnouncementsSent);
        Assert.Equal(7, counters.HostForwardsHeard);
        Assert.Equal(8, counters.HostRepliesForwarded);
        Assert.Equal(9, counters.HostBroadcastReplies);
        Assert.Equal(10, counters.HostAmbiguousReplies);
    }

    [Fact]
    public void Initial_IsIdleAndNotBusy()
    {
        Assert.Equal(SessionMode.None, SessionState.Initial.Mode);
        Assert.Equal(SessionPhase.Idle, SessionState.Initial.Phase);
        Assert.False(SessionState.Initial.IsBusy);
        Assert.False(SessionState.Initial.IsRunning);
    }

    [Theory]
    [InlineData(SessionPhase.Starting, true)]
    [InlineData(SessionPhase.Stopping, true)]
    [InlineData(SessionPhase.Running, false)]
    [InlineData(SessionPhase.Failed, false)]
    public void IsBusy_CoversStartingAndStopping(SessionPhase phase, bool expected)
        => Assert.Equal(expected, (SessionState.Initial with { Phase = phase }).IsBusy);
}
