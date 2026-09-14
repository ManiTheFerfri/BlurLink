using BlurLink.Contracts;
using BlurLink.Core.Session;
using BlurLink.Platform;
using BlurLink.Desktop.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

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
        Assert.Contains("captured=4", vm.RawCounters);
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
        Assert.Contains("captured=0", vm.RawCounters);
    }
}
