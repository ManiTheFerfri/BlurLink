using BlurLink.Core.Validation;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class HexSignatureParserTests
{
    [Fact]
    public void EmptyMeans_NoConstraint()
    {
        Assert.Empty(HexSignatureParser.Parse(null));
        Assert.Empty(HexSignatureParser.Parse(""));
        Assert.Empty(HexSignatureParser.Parse("   "));
    }

    [Fact]
    public void SpacedHex_Parses()
    {
        Assert.Equal(new byte[] { 0x42, 0x4C, 0x55, 0x52 }, HexSignatureParser.Parse("42 4C 55 52"));
    }

    [Fact]
    public void ContinuousRun_Parses()
    {
        Assert.Equal(new byte[] { 0x42, 0x4C, 0x55, 0x52 }, HexSignatureParser.Parse("424C5552"));
    }

    [Theory]
    [InlineData("42:4C:55:52")]
    [InlineData("42-4C-55-52")]
    [InlineData("42,4C,55,52")]
    [InlineData("0x42 0x4C 0x55 0x52")]
    public void Separators_Accepted(string text)
    {
        Assert.Equal(4, HexSignatureParser.Parse(text).Length);
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("4")]
    [InlineData("421")]
    [InlineData("42 4G")]
    public void InvalidHex_Rejected(string text)
    {
        Assert.Throws<FormatException>(() => HexSignatureParser.Parse(text));
    }

    [Fact]
    public void OverlongSignature_Rejected()
    {
        var hex = string.Join(" ", Enumerable.Repeat("AA", 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => HexSignatureParser.Parse(hex));
    }
}
