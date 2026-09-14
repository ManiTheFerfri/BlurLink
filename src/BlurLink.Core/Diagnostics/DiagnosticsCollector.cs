using System.Net.NetworkInformation;
using System.Text;
using BlurLink.Core.Net;

namespace BlurLink.Core.Diagnostics;

/// <summary>
/// Builds the "Copy diagnostics" text: adapter + route metadata only.
/// Never includes packet contents.
/// </summary>
public static class DiagnosticsCollector
{
    public static string BuildDiagnosticsText(
        string hostOverlayIp,
        int? discoveryPort,
        int selectedIfIndex,
        IReadOnlyList<AdapterInfo>? adapters = null,
        RouteInfo? route = null)
    {
        adapters ??= SafeEnumerate();
        route ??= SafeRoute(hostOverlayIp);

        var sb = new StringBuilder();
        sb.AppendLine("BlurLink diagnostics (metadata only, no packet contents)");
        sb.AppendLine($"UTC: {DateTime.UtcNow:O}");
        sb.AppendLine($"OS: {Environment.OSVersion} x64={Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Host overlay IP: {hostOverlayIp}");
        sb.AppendLine($"Discovery UDP port: {(discoveryPort?.ToString() ?? "(research mode — unknown)")}");
        sb.AppendLine($"Selected adapter ifIndex: {selectedIfIndex}");
        sb.AppendLine();

        if (route is not null)
        {
            sb.AppendLine("[Route lookup]");
            sb.AppendLine($"  resolved={route.Resolved} src={route.SelectedSourceAddress} " +
                          $"ifIndex={route.SelectedInterfaceIndex} nextHop={route.NextHop} metric={route.Metric}");
            if (route.Resolved && selectedIfIndex != 0 && route.SelectedInterfaceIndex != selectedIfIndex)
            {
                sb.AppendLine("  WARNING: route does not resolve to the selected overlay adapter.");
            }

            sb.AppendLine();
        }

        sb.AppendLine("[Adapters]");
        foreach (var a in adapters)
        {
            sb.AppendLine($"  - {a.FriendlyName} (ifIndex={a.IfIndex}, status={a.Status}, " +
                          $"virtualHint={a.LooksVirtual})");
            sb.AppendLine($"    desc: {a.Description}");
            sb.AppendLine($"    ipv4: {string.Join(", ", a.Ipv4Addresses)}" +
                          (a.DirectedBroadcast is not null ? $" bcast={a.DirectedBroadcast}" : string.Empty) +
                          (a.Gateway is not null ? $" gw={a.Gateway}" : string.Empty));
        }

        return sb.ToString();
    }

    public static async Task<PingReply?> PingOnceAsync(string host, int timeoutMs = 1500)
    {
        using var ping = new Ping();
        try
        {
            return await ping.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Human-readable ping verdict. Only <see cref="IPStatus.Success"/> counts
    /// as reachable: SendPingAsync returns a non-null PingReply with a failure
    /// status when nothing answers, so "non-null" must never be read as
    /// "replied" (that produced false positives in the GUI).
    /// </summary>
    public static string FormatPingOutcome(IPStatus status, string? address, long roundtripMs)
    {
        if (status == IPStatus.Success)
        {
            return $"Reply from {address}: {roundtripMs}ms";
        }

        return status == IPStatus.Unknown
            ? "No reply (timeout or name resolution failed)."
            : $"No reply ({DescribePingFailure(status)}). Check the VPN/LAN emulator.";
    }

    private static string DescribePingFailure(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "timed out",
        IPStatus.DestinationHostUnreachable => "destination host unreachable",
        IPStatus.DestinationNetworkUnreachable => "destination network unreachable",
        IPStatus.DestinationPortUnreachable => "destination port unreachable",
        IPStatus.DestinationProtocolUnreachable => "destination protocol unreachable",
        IPStatus.DestinationUnreachable => "destination unreachable",
        IPStatus.BadDestination => "bad destination address",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static IReadOnlyList<AdapterInfo> SafeEnumerate()
    {
        try
        {
            return AdapterEnumerator.Enumerate();
        }
        catch
        {
            return Array.Empty<AdapterInfo>();
        }
    }

    private static RouteInfo? SafeRoute(string host)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return RouteResolver.Lookup(host);
        }
        catch
        {
            return null;
        }
    }
}
