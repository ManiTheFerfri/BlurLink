using BlurLink.Core.Session;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class SessionStoryTests
{
    private static SessionState Bridge(long captured, long forwarded) => SessionState.Initial with
    {
        Mode = SessionMode.Bridge,
        Phase = SessionPhase.Running,
        Counters = SessionCounters.Empty with { Captured = captured, Forwarded = forwarded },
    };

    [Fact]
    public void BlockingReason_WinsOverEverything()
    {
        var state = SessionState.Initial with
        {
            BlockingReasons = new[] { new BlockingReason("helper-missing", "The helper is not available.", "Rebuild it with scripts/build.ps1.") },
        };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("blocked", story.Code);
        Assert.Equal("Not ready yet", story.Headline);
        Assert.Equal("The helper is not available.", story.Detail);
        Assert.Equal("Rebuild it with scripts/build.ps1.", story.SuggestedAction);
        Assert.Equal(StorySeverity.Attention, story.Severity);
    }

    [Fact]
    public void Failed_ShowsTheError()
    {
        var story = SessionStoryTable.Describe(
            SessionState.Initial with { Phase = SessionPhase.Failed, LastError = "another BlurLink bridge is already active" });

        Assert.Equal("failed", story.Code);
        Assert.Contains("another BlurLink bridge is already active", story.Detail);
    }

    [Fact]
    public void BridgeWithNothingCaptured_TellsTheUserToRefreshTheGame()
    {
        var story = SessionStoryTable.Describe(Bridge(captured: 0, forwarded: 0));

        Assert.Equal("bridge-idle", story.Code);
        Assert.Contains("refresh", story.SuggestedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BridgeSeeingButNotForwarding_SaysSoWithoutBlamingTheHost()
    {
        var story = SessionStoryTable.Describe(Bridge(captured: 4, forwarded: 0));

        Assert.Equal("bridge-seen-only", story.Code);
        Assert.Contains("discovery port", story.Detail);
    }

    [Fact]
    public void BridgeForwarding_PointsAtHostModeWhenTheLobbyIsMissing()
    {
        var story = SessionStoryTable.Describe(Bridge(captured: 4, forwarded: 4));

        Assert.Equal("bridge-forwarding", story.Code);
        Assert.Contains("Host mode", story.Detail);
    }

    [Fact]
    public void HostWithNoPlayerYet_IsNormal_NotAFailure()
    {
        var state = SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("host-waiting", story.Code);
        Assert.Equal(StorySeverity.Neutral, story.Severity);
        Assert.Contains("search", story.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostHearingNothingFromAPlayer_DoesNotClaimThePlayerIsBlocked()
    {
        var state = SessionState.Initial with
        {
            Mode = SessionMode.Host,
            Phase = SessionPhase.Running,
            Players = new[] { new PlayerFact("10.0.0.200", "192.168.0.200", 50001, ForwardsHeard: 0, InFilter: true) },
        };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("host-no-forward", story.Code);
        Assert.Contains("has not searched yet", story.Detail);
    }

    [Fact]
    public void HostHeardButNotYetForwarded_SaysWhatIsMissing()
    {
        var state = SessionState.Initial with
        {
            Mode = SessionMode.Host,
            Phase = SessionPhase.Running,
            Counters = SessionCounters.Empty with { HostForwardsHeard = 3, HostRepliesForwarded = 0 },
        };

        Assert.Equal("host-heard-no-reply", SessionStoryTable.Describe(state).Code);
    }

    [Fact]
    public void HostForwarding_SaysTheReplyIsOnItsWay()
    {
        var state = SessionState.Initial with
        {
            Mode = SessionMode.Host,
            Phase = SessionPhase.Running,
            Counters = SessionCounters.Empty with { HostForwardsHeard = 3, HostRepliesForwarded = 3 },
        };

        var story = SessionStoryTable.Describe(state);

        Assert.Equal("host-forwarding", story.Code);
        Assert.Equal(StorySeverity.Progress, story.Severity);
    }

    [Fact]
    public void Idle_SaysNothingIsRunning()
        => Assert.Equal("idle", SessionStoryTable.Describe(SessionState.Initial).Code);

    [Fact]
    public void EveryCodeIsDistinct()
    {
        var codes = new[]
        {
            SessionState.Initial with { BlockingReasons = new[] { new BlockingReason("x", "y", "z") } },
            SessionState.Initial with { Phase = SessionPhase.Failed },
            Bridge(0, 0), Bridge(4, 0), Bridge(4, 4),
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running },
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running, Players = new[] { new PlayerFact("a", "b", 1, 0, true) } },
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running, Counters = SessionCounters.Empty with { HostForwardsHeard = 1 } },
            SessionState.Initial with { Mode = SessionMode.Host, Phase = SessionPhase.Running, Counters = SessionCounters.Empty with { HostForwardsHeard = 1, HostRepliesForwarded = 1 } },
            SessionState.Initial,
        }.Select(SessionStoryTable.Describe).Select(s => s.Code).ToArray();

        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
