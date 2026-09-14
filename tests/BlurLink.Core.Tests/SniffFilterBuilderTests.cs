using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class SniffFilterBuilderTests
{
    [Fact]
    public void SingleBroadcast_BuildsNarrowSniffFilter()
    {
        var filter = SniffFilterBuilder.Build(new[] { "255.255.255.255" });
        Assert.Equal("outbound && ip && udp && ip.DstAddr == 255.255.255.255", filter);
    }

    [Fact]
    public void TwoBroadcasts_BuildsOrFilter()
    {
        var filter = SniffFilterBuilder.Build(new[] { "255.255.255.255", "10.88.255.255" });
        Assert.Equal(
            "outbound && ip && udp && (ip.DstAddr == 255.255.255.255 || ip.DstAddr == 10.88.255.255)",
            filter);
    }

    [Fact]
    public void Duplicates_Collapse()
    {
        var filter = SniffFilterBuilder.Build(new[] { "255.255.255.255", "255.255.255.255" });
        Assert.Equal("outbound && ip && udp && ip.DstAddr == 255.255.255.255", filter);
    }

    [Fact]
    public void ConfiguredMulticastListened()
    {
        var filter = SniffFilterBuilder.Build(new[] { "255.255.255.255", "239.255.0.1" });
        Assert.Contains("ip.DstAddr == 239.255.0.1", filter);
    }

    [Fact]
    public void InboundListensToDiscoveryPort()
    {
        var filter = SniffFilterBuilder.Build(Array.Empty<string>(), null, "in", 50001);
        Assert.Equal("inbound && ip && udp && udp.DstPort == 50001", filter);
    }

    [Fact]
    public void InboundRequiresPort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SniffFilterBuilder.Build(Array.Empty<string>(), null, "in", 0));
    }

    [Fact]
    public void UnknownDirection_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            SniffFilterBuilder.Build(new[] { "255.255.255.255" }, null, "sideways", 0));
    }

    [Fact]
    public void OutboundPortGate_Optional()
    {
        var filter = SniffFilterBuilder.Build(new[] { "255.255.255.255" }, null, "out", 50001);
        Assert.Equal(
            "outbound && ip && udp && ip.DstAddr == 255.255.255.255 && udp.DstPort == 50001",
            filter);
    }

    [Fact]
    public void SniffFilter_NeverContainsPorts()
    {
        // The sniffer watches ALL ports by design (it discovers them);
        // narrowness comes from broadcast-only + SNIFF mode + bounded time.
        var filter = SniffFilterBuilder.Build(new[] { "255.255.255.255" });
        Assert.DoesNotContain("DstPort", filter);
        Assert.DoesNotContain("true", filter);
    }

    [Theory]
    [InlineData("100.96.47.177")] // unicast must never be sniffed
    [InlineData("not-an-ip")]
    [InlineData("255.255.255.255 || true")]
    public void InvalidDestinations_Rejected(string dst)
    {
        Assert.ThrowsAny<ArgumentException>(() => SniffFilterBuilder.Build(new[] { dst }));
    }

    [Fact]
    public void EmptyList_Rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => SniffFilterBuilder.Build(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(61)]
    public void BadDurations_Rejected(int secs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SniffFilterBuilder.ValidateSniffParams(secs, 200));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(60)]
    public void GoodDurations_Accepted(int secs)
    {
        SniffFilterBuilder.ValidateSniffParams(secs, 200);
    }
}
