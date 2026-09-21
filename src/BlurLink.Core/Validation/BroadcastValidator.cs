using System.Net;
using System.Net.Sockets;

namespace BlurLink.Core.Validation;

/// <summary>
/// Validates the broadcast/multicast destination the bridge listens for.
/// Allowed: 255.255.255.255, a directed broadcast, or a v4 multicast address.
/// Arbitrary unicast addresses are rejected (that would widen the filter).
/// </summary>
public static class BroadcastValidator
{
    public static bool IsMulticast(IPAddress v4)
    {
        var b = v4.GetAddressBytes()[0];
        return b >= 224 && b <= 239;
    }

    public static bool IsGlobalBroadcast(IPAddress v4)
        => v4.Equals(IPAddress.Broadcast);

    public static void ValidateOrThrow(string? text, string? adapterBroadcast = null)
    {
        if (!Ipv4Validator.TryParse(text, out var addr) || addr is null)
        {
            throw new ArgumentException($"'{text}' is not a valid IPv4 broadcast/multicast destination.", nameof(text));
        }

        if (IsGlobalBroadcast(addr) || IsMulticast(addr))
        {
            return;
        }

        // Directed broadcast: must end in .255 OR match the adapter's computed broadcast.
        var bytes = addr.GetAddressBytes();
        if (bytes[3] == 255)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(adapterBroadcast) &&
            string.Equals(adapterBroadcast.Trim(), addr.ToString(), StringComparison.Ordinal))
        {
            return;
        }

        throw new ArgumentException(
            $"'{text}' is not a broadcast or multicast address. Use 255.255.255.255, a directed broadcast (x.x.x.255), or a multicast address.",
            nameof(text));
    }

    /// <summary>Computes directed broadcast from address + mask (both IPv4).</summary>
    public static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (ip.Length != 4 || m.Length != 4)
        {
            throw new ArgumentException("IPv4 only.");
        }

        var b = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            b[i] = (byte)(ip[i] | ~m[i]);
        }

        return new IPAddress(b);
    }
}
