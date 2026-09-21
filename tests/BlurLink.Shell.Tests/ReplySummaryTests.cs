using BlurLink.Contracts;
using BlurLink.Shell.ViewModels;
using Xunit;

namespace BlurLink.Shell.Tests;

/// <summary>
/// Reply-verdict formatting on the Shell's sniff part. Moved from
/// Core.Tests with the M4 WPF cut (the Desktop JoinViewModel is deleted);
/// the logic is the verbatim port, so the same three assertions hold.
/// </summary>
public sealed class ReplySummaryTests
{
    [Fact]
    public void EmptyResults_AsksForRefresh()
    {
        var s = DiscoverySniffViewModel.FormatReplySummary(
            Array.Empty<SniffPortCount>(), "10.88.132.54");
        Assert.Contains("No replies yet", s);
    }

    [Fact]
    public void HostAnswer_RecognizedByIp()
    {
        var results = new List<SniffPortCount>
        {
            new() { Port = 50001, Count = 3, SrcIp = "10.88.132.54" },
            new() { Port = 50001, Count = 1, SrcIp = "10.9.9.9" },
        };
        var s = DiscoverySniffViewModel.FormatReplySummary(results, "10.88.132.54");
        Assert.Contains("Host answered", s);
        Assert.Contains("10.88.132.54", s);
    }

    [Fact]
    public void ForeignAnswer_Flagged()
    {
        var results = new List<SniffPortCount>
        {
            new() { Port = 50001, Count = 2, SrcIp = "10.9.9.9" },
        };
        var s = DiscoverySniffViewModel.FormatReplySummary(results, "10.88.132.54");
        Assert.Contains("not the host", s);
    }
}
