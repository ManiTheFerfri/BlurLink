using System.Net;
using BlurLink.Core.Validation;

namespace BlurLink.Core.Net;

/// <summary>
/// BlurLink's own host-introduction packet: a player tells the host, over the
/// overlay, which overlay address it holds, which LAN address its Blur sends
/// from, and which Blur source port to expect. Fixed 20-byte layout, so a value
/// inside the contents can never impersonate a field — the same reasoning as
/// the existing shadow-proof JSON key lookup. Carries addresses and a port only;
/// never game, lobby or payload data.
///
/// <see cref="Encode"/> is deliberately lenient (so every rejection path in
/// <see cref="TryDecode"/> is reachable from a test). <see cref="TryDecode"/> is
/// the strict gate, and the only path the host ever acts on.
/// </summary>
public sealed record AnnouncePacket(string OverlayIp, string LanIp, int BlurSourcePort)
{
    public const int Length = 20;
    public const byte Version = 1;

    private static readonly byte[] Magic = { (byte)'B', (byte)'L', (byte)'N', (byte)'K' };

    /// <summary>Encodes to exactly <see cref="Length"/> bytes. Never throws.</summary>
    public static byte[] Encode(AnnouncePacket packet)
    {
        var b = new byte[Length];
        Magic.CopyTo(b, 0);
        b[4] = Version;
        b[5] = 0; // flags, reserved
        WriteIp(b, 6, packet.OverlayIp);
        WriteIp(b, 10, packet.LanIp);
        b[14] = (byte)(packet.BlurSourcePort >> 8);
        b[15] = (byte)(packet.BlurSourcePort & 0xFF);
        // 16..19 stay zero: reserved forward-compatibility space.
        return b;
    }

    /// <summary>
    /// Strict validation. Rejects anything not exactly matching the documented
    /// layout, and never throws on arbitrary input.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out AnnouncePacket? packet, out string reason)
    {
        packet = null;
        reason = string.Empty;

        if (data.Length != Length)
        {
            reason = "bad-length";
            return false;
        }

        for (var i = 0; i < Magic.Length; i++)
        {
            if (data[i] != Magic[i])
            {
                reason = "bad-magic";
                return false;
            }
        }

        if (data[4] != Version)
        {
            reason = "unsupported-version";
            return false;
        }

        var overlay = $"{data[6]}.{data[7]}.{data[8]}.{data[9]}";
        var lan = $"{data[10]}.{data[11]}.{data[12]}.{data[13]}";
        var port = (data[14] << 8) | data[15];

        if (!Ipv4Validator.TryParse(overlay, out var overlayAddr) || overlayAddr is null ||
            overlayAddr.Equals(IPAddress.Any))
        {
            reason = "bad-overlay-address";
            return false;
        }

        if (!Ipv4Validator.TryParse(lan, out var lanAddr) || lanAddr is null ||
            lanAddr.Equals(IPAddress.Any))
        {
            reason = "bad-lan-address";
            return false;
        }

        if (port == 0)
        {
            reason = "zero-port";
            return false;
        }

        packet = new AnnouncePacket(overlay, lan, port);
        return true;
    }

    private static void WriteIp(byte[] b, int offset, string ip)
    {
        if (!Ipv4Validator.TryParse(ip, out var addr) || addr is null)
        {
            addr = IPAddress.Any;
        }

        addr.GetAddressBytes().CopyTo(b, offset);
    }
}
