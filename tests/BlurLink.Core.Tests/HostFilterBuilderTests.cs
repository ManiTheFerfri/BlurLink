using System.Text.RegularExpressions;
using BlurLink.Contracts;
using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public class HostFilterBuilderTests
{
    [Fact]
    public void WithNoPlayers_OnlyTheIntroductionPortIsWatched()
    {
        var f = HostFilterBuilder.Build(50001, Array.Empty<HostPlayerKey>());
        Assert.Equal(
            $"(inbound && ip && udp && udp.DstPort == {BlurLinkConstants.HostAnnounceUdpPort})",
            f);
    }

    [Fact]
    public void WithPlayers_EveryTermIsScopedToThem()
    {
        var f = HostFilterBuilder.Build(50001, new[]
        {
            new HostPlayerKey("192.168.1.50", 51234),
            new HostPlayerKey("10.0.0.9", 40000),
        });

        Assert.Contains("(inbound && ip && udp && udp.DstPort == 47811)", f);
        Assert.Contains(
            "(inbound && ip && udp && udp.DstPort == 50001 && (ip.SrcAddr == 192.168.1.50 || ip.SrcAddr == 10.0.0.9))",
            f);
        Assert.Contains(
            "(outbound && ip && udp && udp.SrcPort == 50001 && (ip.DstAddr == 192.168.1.50 || ip.DstAddr == 10.0.0.9))",
            f);
    }

    [Fact]
    public void NeverMatchesAnythingBeyondThoseAddresses()
    {
        var f = HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("192.168.1.50", 51234) });
        Assert.DoesNotContain("255.255.255.255", f);
        Assert.DoesNotContain("0.0.0.0", f);
        Assert.Equal(3, f.Split("||").Length);
    }

    [Fact]
    public void ThePlayerBlurPortIsDeliberatelyAbsentFromTheFilter()
    {
        // If the reply's destination port ever differs from what we expect, the
        // code-side matcher must be able to SEE and report that. Putting the
        // port in the filter would swallow it silently.
        var f = HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("192.168.1.50", 51234) });
        Assert.DoesNotContain("51234", f);
    }

    [Fact]
    public void AddressesAreCanonicalised()
    {
        var f = HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("  192.168.1.50 ", 1) });
        Assert.Contains("ip.DstAddr == 192.168.1.50", f);
        Assert.DoesNotContain(" 192.168.1.50 ", f);
    }

    [Fact]
    public void DuplicatePlayerAddressesAreCollapsed()
    {
        var f = HostFilterBuilder.Build(50001, new[]
        {
            new HostPlayerKey("192.168.1.50", 1),
            new HostPlayerKey("192.168.1.50", 2),
        });

        // Once in the inbound term, once in the outbound term — and no more,
        // even though two players share the address.
        Assert.Equal(2, Regex.Matches(f, "192\\.168\\.1\\.50").Count);
        Assert.Equal(3, f.Split("||").Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RejectsBadDiscoveryPort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HostFilterBuilder.Build(port, Array.Empty<HostPlayerKey>()));
    }

    [Fact]
    public void RejectsTooManyPlayers()
    {
        var many = Enumerable.Range(0, BlurLinkConstants.HostMaxPlayers + 1)
            .Select(i => new HostPlayerKey($"192.168.1.{i + 1}", 1000 + i))
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => HostFilterBuilder.Build(50001, many));
    }

    [Fact]
    public void AcceptsExactlyTheCap()
    {
        var atCap = Enumerable.Range(0, BlurLinkConstants.HostMaxPlayers)
            .Select(i => new HostPlayerKey($"192.168.1.{i + 1}", 1000 + i))
            .ToArray();

        var f = HostFilterBuilder.Build(50001, atCap);
        Assert.Contains("ip.DstAddr == 192.168.1." + BlurLinkConstants.HostMaxPlayers, f);
    }

    [Fact]
    public void RejectsInvalidPlayerAddress()
    {
        Assert.Throws<ArgumentException>(() =>
            HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("not-an-ip", 1) }));
    }

    [Fact]
    public void RejectsANonCanonicalPlayerAddress()
    {
        Assert.Throws<ArgumentException>(() =>
            HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("192.168.001.050", 1) }));
    }
}
