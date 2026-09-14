using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BlurLink.Core.Net;

/// <summary>Adapter row shown in the GUI pickers.</summary>
public sealed record AdapterInfo(
    string FriendlyName,
    string Description,
    int IfIndex,
    OperationalStatus Status,
    IReadOnlyList<string> Ipv4Addresses,
    string? Gateway,
    string? SubnetMask,
    string? DirectedBroadcast,
    bool LooksVirtual);

public static class AdapterEnumerator
{
    private static readonly string[] VirtualHints =
    {
        "tap", "tun", "vpn", "virtual", "wireguard", "tailscale", "hamachi",
        "radmin", "zero-tier", "zerotier", "radmin", "game", "emulator", "wintun",
        "openvpn", "softether", "nebula", "netbird",
    };

    public static IReadOnlyList<AdapterInfo> Enumerate()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Loopback is never an overlay: hide it to keep the picker short.
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            AdapterInfo? info;
            try
            {
                info = Describe(nic);
            }
            catch
            {
                // Skip adapters that refuse property queries instead of
                // failing the whole enumeration.
                continue;
            }

            if (info is not null)
            {
                result.Add(info);
            }
        }

        // Sort virtual/VPN-looking adapters first, then by name.
        return result
            .OrderByDescending(a => a.LooksVirtual)
            .ThenBy(a => a.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AdapterInfo? Describe(NetworkInterface nic)
    {
        // Skip loopback/tunnel pseudo-interfaces without IPv4.
        var props = nic.GetIPProperties();
        var v4Addrs = props.UnicastAddresses
            .Where(a => a.Address is { AddressFamily: AddressFamily.InterNetwork })
            .ToList();
        if (v4Addrs.Count == 0)
        {
            return null;
        }

        var gateway = props.GatewayAddresses
            .FirstOrDefault(g => g.Address is { AddressFamily: AddressFamily.InterNetwork })
            ?.Address?.ToString();

        string? mask = null;
        string? bcast = null;
        var first = v4Addrs[0];
        try
        {
            mask = first.IPv4Mask?.ToString();
            if (first.Address is not null && first.IPv4Mask is not null)
            {
                bcast = Validation.BroadcastValidator
                    .DirectedBroadcast(first.Address, first.IPv4Mask).ToString();
            }
        }
        catch
        {
            // best effort only
        }

        int ifIndex = 0;
        try
        {
            var p = props.GetIPv4Properties();
            if (p is not null)
            {
                ifIndex = p.Index;
            }
        }
        catch
        {
            // best effort
        }

        var haystack = ((nic.Name ?? string.Empty) + " " + (nic.Description ?? string.Empty)).ToLowerInvariant();
        bool looksVirtual = VirtualHints.Any(h => haystack.Contains(h, StringComparison.Ordinal));

        return new AdapterInfo(
            FriendlyName: string.IsNullOrEmpty(nic.Name) ? "(unnamed adapter)" : nic.Name,
            Description: nic.Description ?? string.Empty,
            IfIndex: ifIndex,
            Status: nic.OperationalStatus,
            Ipv4Addresses: v4Addrs
                .Where(a => a.Address is not null)
                .Select(a => a.Address.ToString())
                .ToArray(),
            Gateway: gateway,
            SubnetMask: mask,
            DirectedBroadcast: bcast,
            LooksVirtual: looksVirtual);
    }
}
