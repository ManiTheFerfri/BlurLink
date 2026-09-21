using System.Collections.ObjectModel;
using BlurLink.Contracts;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Core.Validation;
using BlurLink.Platform;

namespace BlurLink.Shell.ViewModels;

/// <summary>One row of the Join pre-flight checklist.</summary>
public sealed record PreflightItem(string Label, bool Ok);

/// <summary>
/// Join tab part 1 of 3: bridge fields and validation only. No helper IO
/// except adapter enumeration. Port of the matching <c>BlurLink.Desktop</c>
/// <c>JoinViewModel</c> members, verbatim unless noted.
/// </summary>
public sealed class BridgeSettingsViewModel : ShellViewModelBase
{
    private readonly BlurLinkConfig _config;
    private readonly Action _onChanged;
    private readonly Action _capabilitiesChanged;
    private bool _initialized; // set at end of ctor; suppresses save during construction
    private bool _suppressConfigWrite; // true while reloading fields from config (RefreshFromConfig)

    public ObservableCollection<AdapterInfo> Adapters { get; } = new();
    public ObservableCollection<PreflightItem> PreflightItems { get; } = new();

    private AdapterInfo? _selectedAdapter;
    public AdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (Set(ref _selectedAdapter, value) && value is not null)
            {
                if (!_suppressConfigWrite)
                {
                    _config.SelectedAdapterIfIndex = value.IfIndex;
                    if (_initialized)
                    {
                        _onChanged();
                    }
                }

                Raise(nameof(RouteWarning));
                Raise(nameof(RouteDetails));
                RaisePreflight();
            }
        }
    }

    private string _hostIp = string.Empty;
    public string HostIp
    {
        get => _hostIp;
        set
        {
            if (Set(ref _hostIp, value))
            {
                if (!_suppressConfigWrite)
                {
                    _config.HostOverlayIp = value.Trim();
                }

                Raise(nameof(RouteWarning));
                Raise(nameof(RouteDetails));
                RaisePreflight();
            }
        }
    }

    private string _discoveryPort = string.Empty;
    public string DiscoveryPort
    {
        get => _discoveryPort;
        set { if (Set(ref _discoveryPort, value)) { RaisePreflight(); } }
    }

    private string _broadcastDestination = "255.255.255.255";
    public string BroadcastDestination
    {
        get => _broadcastDestination;
        set { if (Set(ref _broadcastDestination, value)) { RaisePreflight(); } }
    }

    private string _payloadHex = string.Empty;
    public string PayloadHex
    {
        get => _payloadHex;
        set { if (Set(ref _payloadHex, value)) { RaisePreflight(); } }
    }

    private bool _preserveBroadcast = true;
    public bool PreserveBroadcast { get => _preserveBroadcast; set => Set(ref _preserveBroadcast, value); }

    public string RouteWarning
    {
        get
        {
            var route = TryRoute();
            if (route is null)
            {
                return string.Empty;
            }

            if (!route.Resolved)
            {
                return "Route lookup failed — verify the host overlay IP is reachable.";
            }

            if (_config.SelectedAdapterIfIndex != 0 && route.SelectedInterfaceIndex != _config.SelectedAdapterIfIndex)
            {
                return $"Warning: route to {HostIp.Trim()} uses ifIndex {route.SelectedInterfaceIndex} (src {route.SelectedSourceAddress}), not your selected adapter.";
            }

            return string.Empty;
        }
    }

    public string RouteDetails
    {
        get
        {
            var route = TryRoute();
            if (route is null)
            {
                return "Route: (enter a host IP to test)";
            }

            return route.Resolved
                ? $"Route: src={route.SelectedSourceAddress} ifIndex={route.SelectedInterfaceIndex} nextHop={route.NextHop} metric={route.Metric}"
                : "Route: unresolved — is the VPN/LAN emulator running?";
        }
    }

    /// <summary>Explicit pre-flight checklist so Start never surprises.</summary>
    public string PreflightChecklist => string.Join("  ",
        ComputePreflight().Select(i => $"[{(i.Ok ? '✓' : '✗')}] {i.Label}"));

    /// <summary>One-line summary: Ready, or the first thing missing.</summary>
    public string PreflightSummary
    {
        get
        {
            var firstBad = ComputePreflight().FirstOrDefault(i => !i.Ok);
            return firstBad is null ? "Ready — start the bridge." : $"To start: fix {firstBad.Label.ToLowerInvariant()}.";
        }
    }

    public bool PreflightReady => ComputePreflight().All(i => i.Ok);

    public RelayCommand RefreshAdaptersCommand { get; }

    public BridgeSettingsViewModel(
        BlurLinkConfig config,
        Action onChanged,
        Action capabilitiesChanged)
    {
        _config = config;
        _onChanged = onChanged;
        _capabilitiesChanged = capabilitiesChanged;

        _hostIp = config.HostOverlayIp;
        _discoveryPort = config.DiscoveryUdpPort?.ToString() ?? BlurLinkConstants.DiscoveryUdpPortDefault.ToString();
        _broadcastDestination = string.IsNullOrWhiteSpace(config.BroadcastDestination) ? "255.255.255.255" : config.BroadcastDestination;
        _payloadHex = config.PayloadPrefixHex;
        _preserveBroadcast = config.PreserveOriginalBroadcast;

        RefreshAdaptersCommand = new RelayCommand(_ => RefreshAdapters());
        RefreshAdapters();
        RaisePreflight();
        _initialized = true;
    }

    private List<PreflightItem> ComputePreflight()
    {
        bool ipOk = Ipv4Validator.TryParse(HostIp?.Trim(), out _);
        bool portOk = int.TryParse(DiscoveryPort?.Trim(), out int p) && p is >= 1 and <= 65535;
        bool bcastOk = true;
        try
        {
            BroadcastValidator.ValidateOrThrow(BroadcastDestination?.Trim(), SelectedAdapter?.DirectedBroadcast);
        }
        catch
        {
            bcastOk = false;
        }

        bool sigOk = true;
        try
        {
            _ = HexSignatureParser.Parse(PayloadHex);
        }
        catch
        {
            sigOk = false;
        }

        bool adapterOk = SelectedAdapter is not null;
        var route = TryRoute();
        bool routeOk = route is { Resolved: true } &&
            (_config.SelectedAdapterIfIndex == 0 || route.SelectedInterfaceIndex == _config.SelectedAdapterIfIndex);
        bool helperOk = HelperLauncher.HelperAvailable;

        return new List<PreflightItem>
        {
            new("Host IP address", ipOk),
            new("Discovery port", portOk),
            new("Broadcast address", bcastOk),
            new("Payload signature", sigOk),
            new("Overlay adapter", adapterOk),
            new("Network route", routeOk),
            new("Helper + driver", helperOk),
        };
    }

    private void RaisePreflight()
    {
        var items = ComputePreflight();
        PreflightItems.Clear();
        foreach (var item in items)
        {
            PreflightItems.Add(item);
        }

        Raise(nameof(PreflightChecklist));
        Raise(nameof(PreflightSummary));
        Raise(nameof(PreflightReady));
        _capabilitiesChanged();
    }

    public bool ValidateInputs(out int port, out string error)
    {
        port = 0;
        if (!Ipv4Validator.TryParse(HostIp?.Trim(), out _))
        {
            error = "Host overlay IP must be a valid IPv4 address (e.g. 100.96.47.177).";
            return false;
        }

        if (!int.TryParse(DiscoveryPort?.Trim(), out port) || port < 1 || port > 65535)
        {
            error = "Discovery UDP port is required in Join mode (Research mode must be resolved first — see Diagnostics).";
            return false;
        }

        try
        {
            BroadcastValidator.ValidateOrThrow(BroadcastDestination?.Trim(), SelectedAdapter?.DirectedBroadcast);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        try
        {
            _ = HexSignatureParser.Parse(PayloadHex);
        }
        catch (Exception ex)
        {
            error = "Payload signature: " + ex.Message;
            return false;
        }

        if (SelectedAdapter is null)
        {
            error = "Select your local overlay adapter.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Re-reads every bound field from the config (after Settings Import/
    /// Reset). Guarded so reloading never writes the config back or re-enters
    /// OnConfigChanged; without this the Join tab kept stale values and the
    /// next Start overwrote the imported config.
    /// </summary>
    public void RefreshFromConfig()
    {
        _suppressConfigWrite = true;
        try
        {
            HostIp = _config.HostOverlayIp ?? string.Empty;
            DiscoveryPort = _config.DiscoveryUdpPort?.ToString() ?? BlurLinkConstants.DiscoveryUdpPortDefault.ToString();
            BroadcastDestination = string.IsNullOrWhiteSpace(_config.BroadcastDestination)
                ? "255.255.255.255"
                : _config.BroadcastDestination;
            PayloadHex = _config.PayloadPrefixHex ?? string.Empty;
            PreserveBroadcast = _config.PreserveOriginalBroadcast;
            var match = Adapters.FirstOrDefault(a => a.IfIndex == _config.SelectedAdapterIfIndex);
            if (match is not null)
            {
                SelectedAdapter = match;
            }
        }
        finally
        {
            _suppressConfigWrite = false;
        }

        Raise(nameof(RouteWarning));
        Raise(nameof(RouteDetails));
        RaisePreflight();
    }

    /// <summary>
    /// Applies a detected discovery port (the sniff part calls this through
    /// the facade's <c>applyPort</c> wiring, never by touching this VM).
    /// Config half of the WPF <c>ApplySniff</c>; the session part sets Message.
    /// </summary>
    public void ApplySniffedPort(int port)
    {
        DiscoveryPort = port.ToString();
        _config.DiscoveryUdpPort = port;
        _onChanged();
        Raise(nameof(RouteWarning));
        Raise(nameof(RouteDetails));
    }

    private RouteInfo? TryRoute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(HostIp))
        {
            return null;
        }

        if (!Ipv4Validator.TryParse(HostIp.Trim(), out _))
        {
            return null;
        }

        try
        {
            return RouteResolver.Lookup(HostIp.Trim());
        }
        catch
        {
            return null;
        }
    }

    public void RefreshAdapters()
    {
        Adapters.Clear();
        try
        {
            foreach (var a in AdapterEnumerator.Enumerate())
            {
                Adapters.Add(a);
            }

            SelectedAdapter = Adapters.FirstOrDefault(a => a.IfIndex == _config.SelectedAdapterIfIndex)
                ?? Adapters.FirstOrDefault(a => a.LooksVirtual)
                ?? Adapters.FirstOrDefault();
        }
        catch (Exception ex)
        {
            // No Message row on this part (it lives on the session part);
            // surface enumeration failures through the log only.
            AppLog.Error("Adapter enumeration failed: " + ex.Message);
        }
    }
}
