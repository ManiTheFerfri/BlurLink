using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Reflection;
using BlurLink.Contracts;
using BlurLink.Core.Config;
using BlurLink.Core.Diagnostics;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Platform;

namespace BlurLink.Shell.ViewModels;

/// <summary>Everything that isn't Join or Host: game path, safety,
/// connection checks, config backup, and about. Port of the WPF
/// SettingsViewModel minus the helper-log viewer, which moved to
/// <see cref="DiagnosticsViewModel"/> (spec §7.3). Exactly three behavior
/// changes, all seam swaps: Clipboard → <see cref="IPlatformServices"/>,
/// OpenFileDialog → <see cref="IPlatformServices.PickExeFileAsync"/>,
/// Process.Start → <see cref="IPlatformServices.OpenFolder"/>.</summary>
public sealed class SettingsViewModel : ShellViewModelBase
{
    private readonly BlurLinkConfig _config;
    private readonly Action _onChanged;
    private readonly Action<string> _applyLogLevel; // validated level -> live logger
    private readonly IPlatformServices _platform;

    private string _blurPath;
    public string BlurPath { get => _blurPath; set => Set(ref _blurPath, value); }

    private string _blurArgs;
    public string BlurArgs { get => _blurArgs; set => Set(ref _blurArgs, value); }

    private string _logLevel;
    public string LogLevel { get => _logLevel; set => Set(ref _logLevel, value); }

    public string[] LogLevels => BlurLinkConstants.LogLevels;

    /// <summary>
    /// Pushes the configured level into the live logger. Save was the only
    /// path doing this: Import and Reset changed LogLevel without applying it,
    /// so the effective level silently kept the previous value (the saved
    /// setting and the logger disagreed, e.g. Reset left Debug chatter on).
    /// </summary>
    private void ApplyLogLevel()
    {
        _config.LogLevel = BlurLinkConstants.NormalizeLogLevel(_config.LogLevel);
        LogLevel = _config.LogLevel;
        _applyLogLevel(_config.LogLevel);
    }

    private int _rateLimit;
    public int RateLimit { get => _rateLimit; set => Set(ref _rateLimit, value); }

    public int MaxRateLimit => BlurLinkConstants.MaxRateLimitPerSecond;

    private string _message = string.Empty;
    public string Message { get => _message; set => Set(ref _message, value); }

    public string LogPath => AppLog.CurrentPath;

    private string _routeResult = "Check that Windows sends overlay traffic the right way.";
    public string RouteResult { get => _routeResult; set => Set(ref _routeResult, value); }

    private string _pingResult = string.Empty;
    public string PingResult { get => _pingResult; set => Set(ref _pingResult, value); }

    private string _exportText = string.Empty;
    public string ExportText { get => _exportText; set => Set(ref _exportText, value); }

    public ObservableCollection<string> AdaptersSummary { get; } = new();

    public string ResearchSteps =>
        "1) On your own LAN with BlurLink off, capture Blur's LAN refresh in Wireshark.\n" +
        "2) Note the UDP destination port, broadcast address, and whether the host reply embeds a LAN IP.\n" +
        "3) Enter the verified values in Join → Advanced.";

    /// <summary>
    /// About line bound by the Settings view. Reads the Shell assembly
    /// version at runtime so the user-visible version can never drift
    /// behind the csproj <c>&lt;Version&gt;</c> again.
    /// </summary>
    public string AboutVersion
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)
                ?? "0.3.0";
            var plus = version.IndexOf('+');
            if (plus >= 0)
            {
                version = version[..plus];
            }

            return $"BlurLink {version} · MIT · No backend, no telemetry, no payload logging. Join mode uses WinDivert (LGPLv3/GPLv2, bundled).";
        }
    }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand RouteTestCommand { get; }
    public RelayCommand PingCommand { get; }
    public RelayCommand RefreshAdaptersCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ImportCommand { get; }

    /// <summary>
    /// <paramref name="applyLogLevel"/> is a seam over the process-global
    /// logger: Save/Import/Reset must push the effective level into it, and
    /// tests need to observe that without racing other test classes.
    /// Production passes nothing and gets AppLog.
    /// <paramref name="platform"/> is the <see cref="IPlatformServices"/> seam
    /// over Clipboard/OpenFileDialog/Process.Start: production always passes
    /// the real one; tests pass the fake. Null gets a throwaway that throws
    /// <see cref="InvalidOperationException"/> on use.
    /// </summary>
    public SettingsViewModel(BlurLinkConfig config, Action onChanged, Action<string>? applyLogLevel = null, IPlatformServices? platform = null)
    {
        _config = config;
        _onChanged = onChanged;
        _applyLogLevel = applyLogLevel ?? (level => AppLog.Initialize(level));
        _platform = platform ?? new ThrowingPlatformServices();
        _blurPath = config.BlurExePath;
        _blurArgs = config.BlurArgs;
        _logLevel = config.LogLevel;
        _rateLimit = config.RateLimitPerSecond;
        BrowseCommand = new RelayCommand(_ => _ = BrowseAsync());
        SaveCommand = new RelayCommand(_ => Save());
        ResetCommand = new RelayCommand(_ => Reset());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        RouteTestCommand = new RelayCommand(_ => RunRouteTest());
        PingCommand = new RelayCommand(_ => _ = PingAsync());
        RefreshAdaptersCommand = new RelayCommand(_ => RefreshAdapters());
        ExportCommand = new RelayCommand(_ => Export());
        ImportCommand = new RelayCommand(_ => Import());
        RefreshAdapters();
    }

    private sealed class ThrowingPlatformServices : IPlatformServices
    {
        public void CopyToClipboard(string text) => throw new InvalidOperationException("No IPlatformServices was provided.");
        public Task<string?> PickExeFileAsync(string initialPath) => throw new InvalidOperationException("No IPlatformServices was provided.");
        public void OpenFolder(string path) => throw new InvalidOperationException("No IPlatformServices was provided.");
    }

    public void RefreshFromConfig()
    {
        BlurPath = _config.BlurExePath;
        BlurArgs = _config.BlurArgs;
        LogLevel = _config.LogLevel;
        RateLimit = _config.RateLimitPerSecond;
        RefreshAdapters();
    }

    private async Task BrowseAsync()
    {
        var picked = await _platform.PickExeFileAsync(BlurPath).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(picked))
        {
            BlurPath = picked;
        }
    }

    private void Save()
    {
        if (!File.Exists(BlurPath) && !string.IsNullOrWhiteSpace(BlurPath))
        {
            Message = "Warning: Blur.exe path does not exist yet — saved anyway.";
        }
        else
        {
            Message = "Settings saved.";
        }

        _config.BlurExePath = BlurPath.Trim();
        _config.BlurArgs = BlurArgs?.Trim() ?? string.Empty;
        _config.LogLevel = BlurLinkConstants.NormalizeLogLevel(LogLevel);
        _config.RateLimitPerSecond = Math.Clamp(RateLimit, 1, BlurLinkConstants.MaxRateLimitPerSecond);
        RateLimit = _config.RateLimitPerSecond;
        ApplyLogLevel();
        AppLog.Info($"Settings saved (logLevel={_config.LogLevel}, rate={RateLimit}/s).");
        _onChanged();
    }

    private void Reset()
    {
        // Full reset of every field, research values included. RefreshFromConfig
        // then reloads the Join fields, so the UI can never keep stale values
        // that a later Start would silently write back.
        var fresh = BlurLinkConfig.CreateDefault();
        _config.BlurExePath = fresh.BlurExePath;
        _config.BlurArgs = fresh.BlurArgs;
        _config.LastMode = fresh.LastMode;
        _config.SelectedAdapterIfIndex = fresh.SelectedAdapterIfIndex;
        _config.HostOverlayIp = fresh.HostOverlayIp;
        _config.DiscoveryUdpPort = fresh.DiscoveryUdpPort;
        _config.BroadcastDestination = fresh.BroadcastDestination;
        _config.PayloadPrefixHex = fresh.PayloadPrefixHex;
        _config.PreserveOriginalBroadcast = fresh.PreserveOriginalBroadcast;
        _config.RateLimitPerSecond = fresh.RateLimitPerSecond;
        _config.LogLevel = fresh.LogLevel;
        ApplyLogLevel();
        RefreshFromConfig();
        _onChanged();
        Message = "Settings reset to safe defaults (research values cleared).";
    }

    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _platform.OpenFolder(dir);
            }
        }
        catch (Exception ex)
        {
            Message = "Could not open log folder: " + ex.Message;
        }
    }

    private void RefreshAdapters()
    {
        AdaptersSummary.Clear();
        try
        {
            foreach (var a in AdapterEnumerator.Enumerate())
            {
                AdaptersSummary.Add($"{a.FriendlyName} [{string.Join(",", a.Ipv4Addresses)}]" +
                    (a.LooksVirtual ? "  ·  likely VPN" : string.Empty));
            }
        }
        catch (Exception ex)
        {
            AdaptersSummary.Add("Enumeration failed: " + ex.Message);
        }
    }

    private void RunRouteTest()
    {
        if (!OperatingSystem.IsWindows())
        {
            RouteResult = "Route lookup requires Windows.";
            return;
        }

        try
        {
            var route = RouteResolver.Lookup(_config.HostOverlayIp);
            RouteResult = route.Resolved
                ? $"To {_config.HostOverlayIp}: via interface {route.SelectedInterfaceIndex} (source {route.SelectedSourceAddress})"
                  + (route.SelectedInterfaceIndex != _config.SelectedAdapterIfIndex && _config.SelectedAdapterIfIndex != 0
                      ? " — WARNING: not your selected adapter." : " — looks good.")
                : $"No route to {_config.HostOverlayIp}. Is the VPN running?";
            AppLog.Info("Route test: " + RouteResult);
        }
        catch (Exception ex)
        {
            RouteResult = "Route test failed: " + ex.Message;
            AppLog.Error("Route test failed: " + ex.Message);
        }
    }

    private async Task PingAsync()
    {
        PingResult = $"Pinging {_config.HostOverlayIp}…";
        var reply = await DiagnosticsCollector.PingOnceAsync(_config.HostOverlayIp).ConfigureAwait(true);
        // Only IPStatus.Success counts: a timed-out ping still yields a
        // non-null PingReply, so checking null alone reported a false success.
        PingResult = reply is null
            ? DiagnosticsCollector.FormatPingOutcome(IPStatus.Unknown, null, 0)
            : DiagnosticsCollector.FormatPingOutcome(reply.Status, reply.Address?.ToString(), reply.RoundtripTime);
        AppLog.Info("Ping: " + PingResult);
    }

    private void Export()
    {
        try
        {
            ExportText = BlurLinkConfigStore.ExportJson(_config);
            Message = "Configuration exported (no payloads).";
        }
        catch (Exception ex)
        {
            Message = "Export failed: " + ex.Message;
        }
    }

    private void Import()
    {
        try
        {
            var cfg = BlurLinkConfigStore.ImportJson(ExportText);
            _config.BlurExePath = cfg.BlurExePath;
            _config.BlurArgs = cfg.BlurArgs;
            _config.LastMode = cfg.LastMode;
            _config.SelectedAdapterIfIndex = cfg.SelectedAdapterIfIndex;
            _config.HostOverlayIp = cfg.HostOverlayIp;
            _config.DiscoveryUdpPort = cfg.DiscoveryUdpPort;
            _config.BroadcastDestination = cfg.BroadcastDestination;
            _config.PayloadPrefixHex = cfg.PayloadPrefixHex;
            _config.PreserveOriginalBroadcast = cfg.PreserveOriginalBroadcast;
            _config.RateLimitPerSecond = cfg.RateLimitPerSecond;
            _config.LogLevel = cfg.LogLevel;
            ApplyLogLevel();
            RefreshFromConfig();
            _onChanged();
            Message = "Configuration imported.";
        }
        catch (Exception ex)
        {
            Message = "Import failed: " + ex.Message;
        }
    }
}
