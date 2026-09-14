using BlurLink.Core.Validation;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class BroadcastValidatorTests
{
    [Theory]
    [InlineData("255.255.255.255")]
    [InlineData("192.168.1.255")] // directed broadcast
    [InlineData("10.0.0.255")]
    [InlineData("239.255.0.1")] // multicast
    [InlineData("224.0.0.251")] // multicast
    public void BroadcastAndMulticast_Accepted(string text)
    {
        BroadcastValidator.ValidateOrThrow(text);
    }

    [Theory]
    [InlineData("192.168.1.100")] // plain unicast
    [InlineData("100.96.47.177")] // overlay unicast: must never be a "broadcast" match
    [InlineData("::1")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void UnicastAndGarbage_Rejected(string text)
    {
        Assert.Throws<ArgumentException>(() => BroadcastValidator.ValidateOrThrow(text));
    }

    [Fact]
    public void AdapterBroadcast_MatchAccepted()
    {
        // Some subnets have non-.255 directed broadcasts; exact match is allowed.
        BroadcastValidator.ValidateOrThrow("10.0.5.191", adapterBroadcast: "10.0.5.191");
    }

    [Fact]
    public void DiscoveryPort_NullMeansResearchMode_RejectedByValidator()
    {
        Assert.Throws<ArgumentException>(() => DiscoveryPortValidator.ValidateOrThrow(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void DiscoveryPort_OutOfRange_Rejected(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscoveryPortValidator.ValidateOrThrow(port));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1234)]
    [InlineData(65535)]
    public void DiscoveryPort_ValidRange_Accepted(int port)
    {
        DiscoveryPortValidator.ValidateOrThrow(port);
    }
}
