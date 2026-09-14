using BlurLink.Core.Validation;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class Ipv4ValidatorTests
{
    [Theory]
    [InlineData("100.96.47.177")]
    [InlineData("255.255.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("  10.0.0.5  ")]
    public void ValidCanonicalAddresses_Parse(string text)
    {
        Assert.True(Ipv4Validator.TryParse(text, out var addr));
        Assert.NotNull(addr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("not-an-ip")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("256.1.1.1")]
    [InlineData("01.2.3.4")] // leading zero: non-canonical
    [InlineData("1.2.3.4/24")] // CIDR
    [InlineData("100.96.47.177:1234")] // with port
    public void InvalidAddresses_Rejected(string? text)
    {
        Assert.False(Ipv4Validator.TryParse(text, out _));
    }

    [Fact]
    public void Ipv6OverlayAddress_IsRejected()
    {
        // v1 is IPv4-only; an IPv6 overlay address must never validate.
        Assert.False(Ipv4Validator.TryParse("fd7a:115c:a1e0::1", out _));
    }
}
