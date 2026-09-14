using System.Buffers.Binary;
using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>Synthetic IPv4/UDP packet transform tests (no real traffic).</summary>
public sealed class PacketTransformerTests
{
    private static byte[] BuildDiscoveryPacket(
        byte[] srcIp, byte[] dstIp, ushort srcPort, ushort dstPort, byte[] payload)
    {
        const int ihl = 20;
        int udpLen = 8 + payload.Length;
        int total = ihl + udpLen;
        var pkt = new byte[total];
        pkt[0] = 0x45; // v4, IHL=5
        pkt[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2, 2), (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(4, 2), 0x1234); // IP ID
        pkt[6] = 0x00; pkt[7] = 0x00; // no fragmentation
        pkt[8] = 64; // TTL
        pkt[9] = 17; // UDP
        // checksum filled below
        Buffer.BlockCopy(srcIp, 0, pkt, 12, 4);
        Buffer.BlockCopy(dstIp, 0, pkt, 16, 4);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(20, 2), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(22, 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(24, 2), (ushort)udpLen);
        Buffer.BlockCopy(payload, 0, pkt, 28, payload.Length);

        var clone = PacketTransformer.CloneWithNewDestination(pkt, dstIp); // self-transform validates checksums
        return clone;
    }

    [Fact]
    public void Transform_OnlyDestinationChanges()
    {
        var payload = new byte[] { 0x42, 0x4C, 0x55, 0x52, 0x01, 0x02 };
        var original = BuildDiscoveryPacket(
            new byte[] { 100, 96, 21, 89 }, new byte[] { 255, 255, 255, 255 },
            50000, 12345, payload);
        var before = (byte[])original.Clone();

        var clone = PacketTransformer.CloneWithNewDestination(
            original, new byte[] { 100, 96, 47, 177 });

        // Original untouched.
        Assert.Equal(before, original);

        // Destination replaced.
        Assert.Equal(new byte[] { 100, 96, 47, 177 }, clone[16..20]);
        // Source identical.
        Assert.Equal(original[12..16], clone[12..16]);
        // UDP ports identical (src + dst).
        Assert.Equal(original[20..24], clone[20..24]);
        // Payload identical.
        Assert.Equal(original[28..], clone[28..]);
        // Same total length.
        Assert.Equal(original.Length, clone.Length);
    }

    [Fact]
    public void Transform_ChecksumsValid()
    {
        var payload = new byte[] { 0x42, 0x4C, 0x55, 0x52 };
        var original = BuildDiscoveryPacket(
            new byte[] { 10, 0, 0, 2 }, new byte[] { 255, 255, 255, 255 },
            40000, 9999, payload);
        var clone = PacketTransformer.CloneWithNewDestination(original, new byte[] { 10, 0, 0, 1 });

        Assert.True(PacketTransformer.VerifyIpChecksum(clone));
    }

    [Fact]
    public void UdpDestinationPort_Unchanged()
    {
        var original = BuildDiscoveryPacket(
            new byte[] { 10, 1, 1, 1 }, new byte[] { 10, 1, 1, 255 },
            61111, 54321, new byte[] { 0x01 });
        var clone = PacketTransformer.CloneWithNewDestination(original, new byte[] { 10, 2, 2, 2 });

        Assert.Equal(
            BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(22, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(clone.AsSpan(22, 2)));
    }

    [Theory]
    [InlineData("ip-fragment")]
    public void Fragments_Rejected(string expectedReason)
    {
        var payload = new byte[] { 0x01, 0x02 };
        var pkt = BuildDiscoveryPacket(
            new byte[] { 10, 0, 0, 2 }, new byte[] { 255, 255, 255, 255 },
            40000, 9999, payload);
        pkt[6] = 0x20; // MF flag => fragment

        Assert.False(PacketTransformer.TryParseUdpOverIpv4(
            pkt, out _, out _, out _, out _, out _, out string? reason));
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Ipv6Bytes_Rejected()
    {
        var fake = new byte[40];
        fake[0] = 0x60; // version 6
        Assert.False(PacketTransformer.TryParseUdpOverIpv4(
            fake, out _, out _, out _, out _, out _, out string? reason));
        Assert.Equal("not-ipv4", reason);
    }

    [Fact]
    public void PayloadPrefix_GatesMatch()
    {
        var payload = new byte[] { 0x42, 0x4C, 0x55, 0x52, 0xFF };
        var pkt = BuildDiscoveryPacket(
            new byte[] { 10, 0, 0, 2 }, new byte[] { 255, 255, 255, 255 },
            40000, 9999, payload);

        Assert.True(PacketTransformer.PayloadStartsWith(pkt, new byte[] { 0x42, 0x4C }));
        Assert.False(PacketTransformer.PayloadStartsWith(pkt, new byte[] { 0x00, 0x01 }));
        Assert.True(PacketTransformer.PayloadStartsWith(pkt, Array.Empty<byte>()));
    }
}
