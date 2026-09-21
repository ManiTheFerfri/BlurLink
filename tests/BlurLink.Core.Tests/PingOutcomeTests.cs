using System.Net.NetworkInformation;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Diagnostics;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Regression: the Ping button treated any non-null PingReply as success, but
/// SendPingAsync returns a non-null reply with a failure status on timeout —
/// so an unreachable host was reported as reachable.
/// </summary>
public sealed class PingOutcomeTests
{
    [Fact]
    public void Success_IsReportedAsReplyFromHost()
    {
        var text = DiagnosticsCollector.FormatPingOutcome(IPStatus.Success, "100.96.47.177", 12);
        Assert.Equal("Reply from 100.96.47.177: 12ms", text);
    }

    [Fact]
    public void TimedOut_IsNeverReportedAsAReachableHost()
    {
        var text = DiagnosticsCollector.FormatPingOutcome(IPStatus.TimedOut, null, 0);
        Assert.DoesNotContain("Reply from", text, StringComparison.Ordinal);
        Assert.Contains("timed out", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unreachable_NamesTheFailure()
    {
        var text = DiagnosticsCollector.FormatPingOutcome(IPStatus.DestinationHostUnreachable, "10.0.0.9", 0);
        Assert.DoesNotContain("Reply from", text, StringComparison.Ordinal);
        Assert.Contains("destination host unreachable", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullReply_IsReportedAsNoReply()
    {
        var text = DiagnosticsCollector.FormatPingOutcome(IPStatus.Unknown, null, 0);
        Assert.Contains("No reply", text, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Locks the helper→GUI status contract for counters the docs tell testers to
/// watch (`dedupSkipped`, `fragmentsRejected`); they were emitted natively but
/// dropped on the managed side.
/// </summary>
public sealed class StatusCounterContractTests
{
    [Fact]
    public void Status_DeserializesDedupSkippedAndFragmentsRejected()
    {
        const string json =
            "{\"type\":\"status\",\"captured\":4,\"fragmentsRejected\":2,\"dedupSkipped\":3}";

        var status = JsonSerializer.Deserialize<IpcStatusResponse>(json);

        Assert.NotNull(status);
        Assert.Equal(4, status!.Captured);
        Assert.Equal(2, status.FragmentsRejected);
        Assert.Equal(3, status.DedupSkipped);
    }
}
