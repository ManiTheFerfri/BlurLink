using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public class AnnouncePacketTests
{
    private static readonly AnnouncePacket Sample = new("25.6.7.8", "192.168.1.50", 51234);

    [Fact]
    public void RoundTrips()
    {
        var bytes = AnnouncePacket.Encode(Sample);
        Assert.Equal(AnnouncePacket.Length, bytes.Length);
        Assert.True(AnnouncePacket.TryDecode(bytes, out var back, out var reason), reason);
        Assert.Equal(Sample, back);
    }

    [Fact]
    public void EncodesTheDocumentedLayout()
    {
        var b = AnnouncePacket.Encode(Sample);
        Assert.Equal(new byte[] { (byte)'B', (byte)'L', (byte)'N', (byte)'K' }, b[..4]);
        Assert.Equal(1, b[4]);                                        // version
        Assert.Equal(0, b[5]);                                        // flags
        Assert.Equal(new byte[] { 25, 6, 7, 8 }, b[6..10]);           // overlay, network order
        Assert.Equal(new byte[] { 192, 168, 1, 50 }, b[10..14]);      // lan, network order
        Assert.Equal(51234, (b[14] << 8) | b[15]);                    // port, big-endian
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, b[16..20]);           // reserved
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(21)]
    public void RejectsWrongLength(int len)
    {
        Assert.False(AnnouncePacket.TryDecode(new byte[len], out var packet, out var reason));
        Assert.Null(packet);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void RejectsBadMagic()
    {
        var b = AnnouncePacket.Encode(Sample);
        b[0] = (byte)'X';
        Assert.False(AnnouncePacket.TryDecode(b, out _, out var reason));
        Assert.Equal("bad-magic", reason);
    }

    [Fact]
    public void RejectsUnknownVersion()
    {
        var b = AnnouncePacket.Encode(Sample);
        b[4] = 99;
        Assert.False(AnnouncePacket.TryDecode(b, out _, out var reason));
        Assert.Equal("unsupported-version", reason);
    }

    [Fact]
    public void RejectsZeroPort()
    {
        // Encode stays lenient so this rejection path is reachable from a test;
        // TryDecode is the strict gate the host actually acts on.
        var b = AnnouncePacket.Encode(Sample with { BlurSourcePort = 0 });
        Assert.False(AnnouncePacket.TryDecode(b, out _, out var reason));
        Assert.Equal("zero-port", reason);
    }

    [Fact]
    public void RejectsZeroOverlayAddress()
    {
        var b = AnnouncePacket.Encode(Sample);
        b[6] = b[7] = b[8] = b[9] = 0;
        Assert.False(AnnouncePacket.TryDecode(b, out _, out var reason));
        Assert.Equal("bad-overlay-address", reason);
    }

    [Fact]
    public void RejectsZeroLanAddress()
    {
        var b = AnnouncePacket.Encode(Sample);
        b[10] = b[11] = b[12] = b[13] = 0;
        Assert.False(AnnouncePacket.TryDecode(b, out _, out var reason));
        Assert.Equal("bad-lan-address", reason);
    }

    [Fact]
    public void DecodeNeverThrowsOnGarbage()
    {
        var rng = new Random(1234);
        for (var i = 0; i < 5000; i++)
        {
            var buf = new byte[rng.Next(0, 64)];
            rng.NextBytes(buf);
            _ = AnnouncePacket.TryDecode(buf, out _, out _); // must never throw
        }
    }

    [Fact]
    public void ValidLookingReservedBytesAreIgnored()
    {
        // Reserved bytes are forward-compatibility space: their contents must
        // never influence acceptance or the decoded values.
        var a = AnnouncePacket.Encode(Sample);
        a[16] = a[17] = a[18] = a[19] = 0xFF;
        Assert.True(AnnouncePacket.TryDecode(a, out var decoded, out var reason), reason);
        Assert.Equal(Sample, decoded);
    }
}
