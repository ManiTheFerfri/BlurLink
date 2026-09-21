using BlurLink.Core.Verify;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class PrefixStabilityTests
{
    [Fact]
    public void ThreeAgreeingSamples_ReturnTheTwelveByteRun()
    {
        var result = PrefixStability.Check(new[]
        {
            "0F 00 00 00 00 00 00 2C 01 00 00 00 AA BB CC DD",
            "0f0000000000002c0100000011223344",
            "0F 00 00 00 00 00 00 2C 01 00 00 00 99 88 77 66",
        });

        Assert.True(result.Agreed);
        Assert.Equal("0F 00 00 00 00 00 00 2C 01 00 00 00", result.PrefixHex);
    }

    [Fact]
    public void OneDifferingByte_RefusesWithAReason()
    {
        var result = PrefixStability.Check(new[]
        {
            "0F 00 00 00 00 00 00 2C 01 00 00 00 AA",
            "1F 00 00 00 00 00 00 2C 01 00 00 00 BB",
            "0F 00 00 00 00 00 00 2C 01 00 00 00 CC",
        });

        Assert.False(result.Agreed);
        Assert.NotEmpty(result.Reason);
        Assert.Equal(string.Empty, result.PrefixHex);
    }

    [Fact]
    public void FewerThanThreeSamples_NeverAgrees()
    {
        var result = PrefixStability.Check(new[] { "0F 00 00", "0F 00 00" });

        Assert.False(result.Agreed);
    }

    [Fact]
    public void GarbageSamples_AreIgnored_NotFatal()
    {
        var result = PrefixStability.Check(new[] { "not hex", "", "0F 00 00 00 00 00 00 2C 01 00 00 00" });

        Assert.False(result.Agreed);
        Assert.Contains("need", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
