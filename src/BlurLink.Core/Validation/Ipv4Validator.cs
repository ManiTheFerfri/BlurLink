using System.Net;
using System.Net.Sockets;

namespace BlurLink.Core.Validation;

/// <summary>Strict IPv4 validation. Rejects IPv6, hostnames, empties, CIDR.</summary>
public static class Ipv4Validator
{
    public static bool TryParse(string? text, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (!IPAddress.TryParse(text, out var parsed))
        {
            return false;
        }

        if (parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        // Round-trip check rejects leading zeros / shorthand oddities.
        var bytes = parsed.GetAddressBytes();
        var canonical = new IPAddress(bytes).ToString();
        if (!string.Equals(canonical, text, StringComparison.Ordinal))
        {
            // Allow only canonical dotted-quad; "1.2.3.4" passes, "01.2.3.4" fails.
            return false;
        }

        address = parsed;
        return true;
    }

    public static void ValidateOrThrow(string? text, string paramName = "hostOverlayIp")
    {
        if (!TryParse(text, out _))
        {
            throw new ArgumentException($"'{text}' is not a valid canonical IPv4 address (e.g. 100.96.47.177).", paramName);
        }
    }
}
