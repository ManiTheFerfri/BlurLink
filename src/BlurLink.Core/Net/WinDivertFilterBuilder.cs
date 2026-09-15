using System.Net;
using BlurLink.Core.Validation;

namespace BlurLink.Core.Net;

/// <summary>
/// Builds a narrow WinDivert NETWORK-layer filter for outbound Blur discovery.
/// Never concatenates unvalidated user text: every parameter is parsed first
/// and only canonical numeric forms are interpolated.
/// </summary>
public static class WinDivertFilterBuilder
{
    public sealed record FilterInput(
        int DiscoveryUdpPort,
        string BroadcastDestination,
        string? AdapterBroadcast = null,
        int? AdapterIfIndex = null);

    public static string Build(FilterInput input)
    {
        DiscoveryPortValidator.ValidateOrThrow(input.DiscoveryUdpPort);
        BroadcastValidator.ValidateOrThrow(input.BroadcastDestination, input.AdapterBroadcast);

        if (!Ipv4Validator.TryParse(input.BroadcastDestination.Trim(), out var bcast) || bcast is null)
        {
            throw new ArgumentException("Invalid broadcast destination.", nameof(input));
        }

        uint ip = ToUInt32BE(bcast);
        // Canonical decimal octet rendering — no user text survives into the filter.
        string ipLiteral = $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

        // Narrow: outbound IPv4 UDP only, exact dst port + exact dst address.
        // "true"/"udp"-style broad filters are intentionally impossible here.
        // An adapter index scopes the same narrow filter to one interface;
        // absent (or non-positive) means every interface, exactly as before.
        var filter = $"outbound && ip && udp && udp.DstPort == {input.DiscoveryUdpPort} && ip.DstAddr == {ipLiteral}";
        if (input.AdapterIfIndex is > 0)
        {
            filter += $" && ifIdx == {input.AdapterIfIndex}";
        }

        return filter;
    }

    private static uint ToUInt32BE(IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
