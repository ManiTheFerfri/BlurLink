using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class WinDivertFilterBuilderTests
{
    [Fact]
    public void ValidInputs_CreateNarrowFilter()
    {
        var filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(
            DiscoveryUdpPort: 12345,
            BroadcastDestination: "255.255.255.255"));

        Assert.Equal(
            "outbound && ip && udp && udp.DstPort == 12345 && ip.DstAddr == 255.255.255.255",
            filter);
    }

    [Fact]
    public void DirectedBroadcast_CreatesExactFilter()
    {
        var filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(
            DiscoveryUdpPort: 9999,
            BroadcastDestination: "192.168.1.255"));

        Assert.Contains("udp.DstPort == 9999", filter);
        Assert.Contains("ip.DstAddr == 192.168.1.255", filter);
        Assert.StartsWith("outbound && ip && udp", filter);
    }

    [Fact]
    public void Filter_IsNeverBroad()
    {
        var filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(5000, "255.255.255.255"));
        Assert.NotEqual("udp", filter);
        Assert.NotEqual("true", filter);
        Assert.DoesNotContain("==", filter.Replace("udp.DstPort ==", "").Replace("ip.DstAddr ==", ""));
    }

    [Fact]
    public void UnicastDestination_Rejected()
    {
        Assert.Throws<ArgumentException>(() => WinDivertFilterBuilder.Build(
            new WinDivertFilterBuilder.FilterInput(5000, "100.96.47.177")));
    }

    [Fact]
    public void FilterInjectionAttempts_Rejected()
    {
        // User text must never survive into the filter; these are not valid
        // destinations at all, so validation rejects them before interpolation.
        Assert.ThrowsAny<ArgumentException>(() => WinDivertFilterBuilder.Build(
            new WinDivertFilterBuilder.FilterInput(5000, "255.255.255.255 || true")));
        Assert.ThrowsAny<ArgumentException>(() => WinDivertFilterBuilder.Build(
            new WinDivertFilterBuilder.FilterInput(5000, "1.1.1.1; udp.SrcPort == 1")));
        Assert.ThrowsAny<ArgumentException>(() => WinDivertFilterBuilder.Build(
            new WinDivertFilterBuilder.FilterInput(0, "255.255.255.255")));
    }
}
