using BlurLink.Core.Validation;

namespace BlurLink.Core.Net;

/// <summary>
/// Builds research sniffer filters. Two modes:
/// - out: outbound broadcast/multicast UDP, any port (discovers the port).
/// - in: inbound UDP TO the given port (detects host replies).
/// SNIFF mode only: packets flow untouched, counts only, bounded time.
/// Every address/port is validated first; only canonical forms are used.
/// </summary>
public static class SniffFilterBuilder
{
    public const int MinDurationSec = 5;
    public const int MaxDurationSec = 60;
    public const int MinMaxPackets = 10;
    public const int MaxMaxPackets = 500;
    public const int MaxBroadcasts = 4;

    public static string Build(
        IReadOnlyList<string> broadcastDestinations,
        string? adapterBroadcast = null,
        string direction = "out",
        int port = 0)
    {
        if (direction == "in")
        {
            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port),
                    "Reply listening needs the discovery port (detect it first).");
            }

            return $"inbound && ip && udp && udp.DstPort == {port}";
        }

        if (direction != "out")
        {
            throw new ArgumentException($"Unknown sniff direction '{direction}'.", nameof(direction));
        }

        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (broadcastDestinations is null || broadcastDestinations.Count == 0)
        {
            throw new ArgumentException("At least one broadcast destination is required.", nameof(broadcastDestinations));
        }

        if (broadcastDestinations.Count > MaxBroadcasts)
        {
            throw new ArgumentOutOfRangeException(nameof(broadcastDestinations), $"At most {MaxBroadcasts} destinations.");
        }

        var canonical = new List<string>(broadcastDestinations.Count);
        foreach (var raw in broadcastDestinations)
        {
            BroadcastValidator.ValidateOrThrow(raw, adapterBroadcast);
            if (!Validation.Ipv4Validator.TryParse(raw.Trim(), out var addr) || addr is null)
            {
                throw new ArgumentException($"Invalid broadcast destination: '{raw}'.", nameof(broadcastDestinations));
            }

            var b = addr.GetAddressBytes();
            canonical.Add($"{b[0]}.{b[1]}.{b[2]}.{b[3]}");
        }

        var distinct = canonical.Distinct().ToArray();
        var terms = string.Join(" || ", distinct.Select(ip => $"ip.DstAddr == {ip}"));
        var addrFilter = distinct.Length == 1 ? terms : $"({terms})";
        var portFilter = port == 0 ? string.Empty : $" && udp.DstPort == {port}";
        return $"outbound && ip && udp && {addrFilter}{portFilter}";
    }

    public static void ValidateSniffParams(int durationSec, int maxPackets)
    {
        if (durationSec is < MinDurationSec or > MaxDurationSec)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSec),
                $"Sniff duration must be {MinDurationSec}-{MaxDurationSec}s.");
        }

        if (maxPackets is < MinMaxPackets or > MaxMaxPackets)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPackets),
                $"Packet cap must be {MinMaxPackets}-{MaxMaxPackets}.");
        }
    }
}
