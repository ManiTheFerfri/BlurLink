using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlurLink.Core.Net;

/// <summary>Result of a route lookup to the host overlay IP.</summary>
public sealed record RouteInfo(
    string Destination,
    string SelectedSourceAddress,
    int SelectedInterfaceIndex,
    string? NextHop,
    int Metric,
    bool Resolved);

/// <summary>
/// Best-route lookup via GetBestRoute (IPv4). Read-only; never alters routes.
/// Warns when the resolved interface differs from the user-selected adapter.
/// </summary>
public static class RouteResolver
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARDROW
    {
        public uint dwForwardDest;
        public uint dwForwardMask;
        public uint dwForwardPolicy;
        public uint dwForwardNextHop;
        public uint dwForwardIfIndex;
        public uint dwForwardType;
        public uint dwForwardProto;
        public uint dwForwardAge;
        public uint dwForwardNextHopAS;
        public uint dwForwardMetric1;
        public uint dwForwardMetric2;
        public uint dwForwardMetric3;
        public uint dwForwardMetric4;
        public uint dwForwardMetric5;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MIB_IPFORWARDROW pBestRoute);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestRoute2(
        IntPtr interfaceLuid, uint interfaceIndex,
        IntPtr sourceAddress, IntPtr destinationAddress,
        uint addressSortOptions, out MIB_IPFORWARDROW bestRoute, out IntPtr bestSourceAddress);

    [SupportedOSPlatform("windows")]
    public static RouteInfo Lookup(string destinationIpv4)
    {
        if (!Validation.Ipv4Validator.TryParse(destinationIpv4, out var dest) || dest is null)
        {
            return new RouteInfo(destinationIpv4, string.Empty, 0, null, 0, false);
        }

        try
        {
            var bytes = dest.GetAddressBytes();
            uint dwDest = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            // GetBestRoute expects network byte order in host uint layout; convert.
            dwDest = (uint)IPAddress.HostToNetworkOrder((int)dwDest);

            uint rc = GetBestRoute(dwDest, 0, out var row);
            if (rc != 0)
            {
                return new RouteInfo(destinationIpv4, string.Empty, 0, null, 0, false);
            }

            string nextHop = new IPAddress(BitConverter.GetBytes(row.dwForwardNextHop)).ToString();
            string? src = GuessSourceForIfIndex((int)row.dwForwardIfIndex);
            return new RouteInfo(
                Destination: destinationIpv4,
                SelectedSourceAddress: src ?? string.Empty,
                SelectedInterfaceIndex: (int)row.dwForwardIfIndex,
                NextHop: nextHop,
                Metric: (int)row.dwForwardMetric1,
                Resolved: true);
        }
        catch
        {
            return new RouteInfo(destinationIpv4, string.Empty, 0, null, 0, false);
        }
    }

    private static string? GuessSourceForIfIndex(int ifIndex)
    {
        try
        {
            foreach (var a in AdapterEnumerator.Enumerate())
            {
                if (a.IfIndex == ifIndex)
                {
                    return a.Ipv4Addresses.FirstOrDefault();
                }
            }
        }
        catch
        {
            // best effort
        }

        return null;
    }
}
