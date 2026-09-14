using BlurLink.Contracts;
using BlurLink.Core.Validation;

namespace BlurLink.Core.Net;

/// <summary>A player as the host knows them: where that player's Blur replies go.</summary>
public readonly record struct HostPlayerKey(string LanIp, int BlurSourcePort);

/// <summary>
/// Builds the host-mode WinDivert filter: three OR-ed terms, every one scoped to
/// accepted players, so the elevated helper is never handed traffic the feature
/// does not need.
///
/// The player's Blur source port is deliberately *absent* from the filter. If the
/// host's reply ever arrives on an unexpected destination port, the code-side
/// matcher must be able to see and report that; a port term here would swallow it
/// silently.
/// </summary>
public static class HostFilterBuilder
{
    public static string Build(int discoveryPort, IReadOnlyList<HostPlayerKey> players)
    {
        if (discoveryPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(discoveryPort),
                "Host mode needs the discovery port (detect it first).");
        }

        players ??= Array.Empty<HostPlayerKey>();
        if (players.Count > BlurLinkConstants.HostMaxPlayers)
        {
            throw new ArgumentOutOfRangeException(nameof(players),
                $"At most {BlurLinkConstants.HostMaxPlayers} players.");
        }

        var canonical = new List<string>(players.Count);
        foreach (var player in players)
        {
            if (!Ipv4Validator.TryParse(player.LanIp?.Trim(), out var addr) || addr is null)
            {
                throw new ArgumentException($"Invalid player address: '{player.LanIp}'.", nameof(players));
            }

            var b = addr.GetAddressBytes();
            canonical.Add($"{b[0]}.{b[1]}.{b[2]}.{b[3]}");
        }

        // Distinct preserves order and collapses two players behind the same
        // LAN address into one address term, which is all the filter needs.
        var distinct = canonical.Distinct().ToArray();

        var terms = new List<string>
        {
            $"(inbound && ip && udp && udp.DstPort == {BlurLinkConstants.HostAnnounceUdpPort})",
        };

        if (distinct.Length > 0)
        {
            var srcTerms = string.Join(" || ", distinct.Select(ip => $"ip.SrcAddr == {ip}"));
            var dstTerms = string.Join(" || ", distinct.Select(ip => $"ip.DstAddr == {ip}"));
            terms.Add($"(inbound && ip && udp && udp.DstPort == {discoveryPort} && ({srcTerms}))");
            terms.Add($"(outbound && ip && udp && udp.SrcPort == {discoveryPort} && ({dstTerms}))");
        }

        return string.Join(" || ", terms);
    }
}
