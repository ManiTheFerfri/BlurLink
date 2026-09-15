using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class FilterIfIndexTests
{
    [Fact]
    public void BridgeFilter_WithoutAdapter_HasNoIfIdxTerm()
    {
        var filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(50001, "255.255.255.255"));

        Assert.DoesNotContain("ifIdx", filter);
    }

    [Fact]
    public void BridgeFilter_WithAdapter_ScopesToIt()
    {
        var filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(50001, "255.255.255.255") with { AdapterIfIndex = 22 });

        Assert.Contains("ifIdx == 22", filter);
    }

    [Fact]
    public void HostFilter_WithAdapter_ScopesAllThreeTerms()
    {
        var filter = HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("192.168.0.200", 50001) }, adapterIfIndex: 22);

        Assert.Equal(3, filter.Split("ifIdx == 22").Length - 1);
    }

    [Fact]
    public void HostFilter_WithoutAdapter_IsUnchanged()
    {
        var filter = HostFilterBuilder.Build(50001, new[] { new HostPlayerKey("192.168.0.200", 50001) });

        Assert.DoesNotContain("ifIdx", filter);
    }
}
