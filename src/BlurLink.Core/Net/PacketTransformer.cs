using System.Buffers.Binary;

namespace BlurLink.Core.Net;

/// <summary>
/// Pure managed IPv4/UDP packet transform used for unit tests and as the
/// documented reference for the native helper's transform:
/// only the IPv4 destination changes; payload and UDP dst port are identical;
/// IP + UDP checksums are recomputed; the original buffer is never mutated.
/// </summary>
public static class PacketTransformer
{
    public sealed record TransformResult(byte[] ClonedPacket, ushort OrigSrcPort, ushort OrigDstPort);

    public static bool TryParseUdpOverIpv4(
        ReadOnlySpan<byte> packet,
        out ushort srcPort,
        out ushort dstPort,
        out int ipHeaderLength,
        out int udpPayloadOffset,
        out int udpPayloadLength,
        out string? rejectReason)
    {
        srcPort = 0;
        dstPort = 0;
        ipHeaderLength = 0;
        udpPayloadOffset = 0;
        udpPayloadLength = 0;
        rejectReason = null;

        if (packet.Length < 20)
        {
            rejectReason = "too-short-for-ipv4";
            return false;
        }

        byte verIhl = packet[0];
        if ((verIhl >> 4) != 4)
        {
            rejectReason = "not-ipv4";
            return false;
        }

        int ihl = (verIhl & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl + 8)
        {
            rejectReason = "bad-ihl-or-too-short-for-udp";
            return false;
        }

        // Reject fragments: MF flag or fragment offset != 0.
        ushort flagsFrag = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        if ((flagsFrag & 0x3FFF) != 0)
        {
            rejectReason = "ip-fragment";
            return false;
        }

        if (packet[9] != 17) // protocol UDP
        {
            rejectReason = "not-udp";
            return false;
        }

        ushort totalLen = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (totalLen > packet.Length)
        {
            rejectReason = "truncated";
            return false;
        }

        srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl, 2));
        dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl + 2, 2));
        ushort udpLen = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl + 4, 2));
        if (udpLen < 8 || ihl + udpLen > packet.Length)
        {
            rejectReason = "bad-udp-length";
            return false;
        }

        ipHeaderLength = ihl;
        udpPayloadOffset = ihl + 8;
        udpPayloadLength = udpLen - 8;
        return true;
    }

    /// <summary>
    /// Clone the packet, replace IPv4 dst with <paramref name="newDstIpv4"/>,
    /// recompute checksums. Returns the clone; input is untouched.
    /// </summary>
    public static byte[] CloneWithNewDestination(byte[] original, byte[] newDstIpv4)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(newDstIpv4);
        if (newDstIpv4.Length != 4)
        {
            throw new ArgumentException("IPv4 only.", nameof(newDstIpv4));
        }

        if (!TryParseUdpOverIpv4(original, out _, out _, out int ihl, out _, out _,
                out string? reject) || reject is not null)
        {
            throw new InvalidOperationException($"Unsupported packet: {reject}.");
        }

        var clone = (byte[])original.Clone();
        clone[16] = newDstIpv4[0];
        clone[17] = newDstIpv4[1];
        clone[18] = newDstIpv4[2];
        clone[19] = newDstIpv4[3];

        // Recompute IPv4 header checksum.
        clone[10] = 0;
        clone[11] = 0;
        ushort ipSum = Checksum(clone.AsSpan(0, ihl));
        BinaryPrimitives.WriteUInt16BigEndian(clone.AsSpan(10, 2), ipSum);

        // Recompute UDP checksum (0 = valid "no checksum" for IPv4; we always set real one).
        int udpOffset = ihl;
        ushort udpLen = BinaryPrimitives.ReadUInt16BigEndian(clone.AsSpan(udpOffset + 4, 2));
        clone[udpOffset + 6] = 0;
        clone[udpOffset + 7] = 0;
        ushort udpSum = UdpChecksum(
            sourceIp: clone.AsSpan(12, 4),
            destIp: clone.AsSpan(16, 4),
            udpSegment: clone.AsSpan(udpOffset, udpLen));
        BinaryPrimitives.WriteUInt16BigEndian(clone.AsSpan(udpOffset + 6, 2), udpSum == 0 ? (ushort)0xFFFF : udpSum);

        return clone;
    }

    public static bool PayloadStartsWith(ReadOnlySpan<byte> packet, byte[] prefix)
    {
        if (prefix.Length == 0)
        {
            return true;
        }

        if (!TryParseUdpOverIpv4(packet, out _, out _, out _, out int off, out int len, out _))
        {
            return false;
        }

        if (len < prefix.Length)
        {
            return false;
        }

        return packet.Slice(off, prefix.Length).SequenceEqual(prefix);
    }

    internal static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        while (i + 1 < data.Length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2));
            i += 2;
        }

        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    internal static ushort UdpChecksum(ReadOnlySpan<byte> sourceIp, ReadOnlySpan<byte> destIp, ReadOnlySpan<byte> udpSegment)
    {
        uint sum = 0;
        sum += BinaryPrimitives.ReadUInt16BigEndian(sourceIp.Slice(0, 2));
        sum += BinaryPrimitives.ReadUInt16BigEndian(sourceIp.Slice(2, 2));
        sum += BinaryPrimitives.ReadUInt16BigEndian(destIp.Slice(0, 2));
        sum += BinaryPrimitives.ReadUInt16BigEndian(destIp.Slice(2, 2));
        sum += 17; // protocol UDP
        sum += (uint)udpSegment.Length;

        int i = 0;
        while (i + 1 < udpSegment.Length)
        {
            // Skip checksum field itself (bytes 6-7 of UDP header).
            if (i == 6)
            {
                i += 2;
                continue;
            }

            sum += BinaryPrimitives.ReadUInt16BigEndian(udpSegment.Slice(i, 2));
            i += 2;
        }

        if (i < udpSegment.Length)
        {
            sum += (uint)(udpSegment[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    /// <summary>Verifies IP header checksum is correct (for tests).</summary>
    public static bool VerifyIpChecksum(ReadOnlySpan<byte> packet)
    {
        int ihl = (packet[0] & 0x0F) * 4;
        return Checksum(packet.Slice(0, ihl)) == 0;
    }
}
