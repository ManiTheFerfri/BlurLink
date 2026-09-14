using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Core.Validation;
using BlurLink.Desktop.Services;

namespace BlurLink.Desktop.ViewModels;

/// <summary>One row of the Join pre-flight checklist.</summary>
public sealed record PreflightItem(string Label, bool Ok);

public sealed class JoinViewModel : ViewModelBase, IDisposable
{
    private readonly BlurLinkConfig _config;
    private readonly Action _onChanged;
    private readonly IHelperProcess _launcher;
    private readonly Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> _connect;
    private readonly Action _dropConnection;
    private readonly Action<string> _status;
    private readonly BlurProcessWatcher _blurWatcher = new();
    private readonly SynchronizationContext? _ui;
    private readonly Timer _rescanTimer;
    private bool _disposed;
    private bool _initialized; // set at end of ctor; suppresses save during construction
    private bool _suppressConfigWrite; // true while reloading fields from config (RefreshFromConfig)
    private int _pollFailures;
    private bool _helperSessionOk; // true after an authenticated status reply
    private bool _busy; // an explicit Start/Stop/Detect operation is in flight

    public ObservableCollection<AdapterInfo> Adapters { get; } = new();
    public ObservableCollection<string> RecentEvents { get; } = new();
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

    private string _hostIp;
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

    private bool _stopWhenBlurExits = true;
    public bool StopWhenBlurExits { get => _stopWhenBlurExits; set => Set(ref _stopWhenBlurExits, value); }

    private bool _bridgeRunning;
    public bool BridgeRunning
    {
        get => _bridgeRunning;
        set
        {
            if (Set(ref _bridgeRunning, value))
            {
                StartStopCommand.RaiseCanExecuteChanged();
                ForceKillCommand.RaiseCanExecuteChanged();
                Raise(nameof(StartStopText));
                Raise(nameof(ShowStart));
                Raise(nameof(ShowForceKill));
            }
        }
    }

    private bool _helperLost;
    public bool HelperLost
    {
        get => _helperLost;
        set
        {
            if (Set(ref _helperLost, value))
            {
                ForceKillCommand.RaiseCanExecuteChanged();
                Raise(nameof(ShowForceKill));
            }
        }
    }

    private bool _sniffRunning;
    public bool SniffRunning
    {
        get => _sniffRunning;
        set
        {
            if (Set(ref _sniffRunning, value))
            {
                DetectCommand.RaiseCanExecuteChanged();
                ListenRepliesCommand.RaiseCanExecuteChanged();
                StopSniffCommand.RaiseCanExecuteChanged();
                ApplySniffCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _sniffStatus = string.Empty;
    public string SniffStatus { get => _sniffStatus; set => Set(ref _sniffStatus, value); }

    private string _repliesSummary = string.Empty;
    public string RepliesSummary { get => _repliesSummary; set => Set(ref _repliesSummary, value); }

    /// <summary>One-line reply verdict for the main card. Pure for tests.</summary>
    public static string FormatReplySummary(IReadOnlyList<SniffPortCount> results, string hostIp)
    {
        if (results.Count == 0)
        {
            return "No replies yet — refresh Blur's LAN list.";
        }

        var top = results.OrderByDescending(r => r.Count).First();
        var from = string.IsNullOrWhiteSpace(top.SrcIp) ? $"port {top.Port}" : $"{top.SrcIp}:{top.Port}";
        return string.Equals(top.SrcIp, hostIp, StringComparison.Ordinal)
            ? $"Host answered ({from} × {top.Count})."
            : $"Answer from {from} (not the host) × {top.Count}.";
    }

    private int _sniffCandidate;
    public int SniffCandidate
    {
        get => _sniffCandidate;
        set
        {
            if (Set(ref _sniffCandidate, value))
            {
                ApplySniffCommand.RaiseCanExecuteChanged();
                Raise(nameof(ShowApplySniff));
            }
        }
    }

    public bool ShowApplySniff => SniffCandidate > 0 && !SniffRunning && !_sniffReplies;

    private bool _sniffReplies; // current/last listen targeted replies, not discovery

    private string _message = string.Empty;
    public string Message { get => _message; set => Set(ref _message, value); }

    private string _blurStatus = "Blur: not running.";
    public string BlurStatus { get => _blurStatus; set => Set(ref _blurStatus, value); }

    private string _counters = "captured=0 forwarded=0 reinjected=0 dropped=0 errors=0 frags=0 dedup=0";
    public string Counters { get => _counters; set => Set(ref _counters, value); }

    private string _activeFilter = string.Empty;
    public string ActiveFilter { get => _activeFilter; set => Set(ref _activeFilter, value); }

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

    public string StartStopText => BridgeRunning ? "Stop Bridge" : "Start Bridge";

    public bool ShowStart => !BridgeRunning;

    public bool ShowForceKill => BridgeRunning || HelperLost;

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
    }

    public RelayCommand RefreshAdaptersCommand { get; }
    public RelayCommand StartStopCommand { get; }
    public RelayCommand ForceKillCommand { get; }
    public RelayCommand LaunchBlurCommand { get; }
    public RelayCommand PollStatusCommand { get; }
    public RelayCommand DetectCommand { get; }
    public RelayCommand ListenRepliesCommand { get; }
    public RelayCommand StopSniffCommand { get; }
    public RelayCommand ApplySniffCommand { get; }

    private CancellationTokenSource? _pollCts;

    public JoinViewModel(
        BlurLinkConfig config,
        Action onChanged,
        IHelperProcess launcher,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect,
        Action dropConnection,
        Action<string> status)
    {
        _config = config;
        _onChanged = onChanged;
        _launcher = launcher;
        _connect = connect;
        _dropConnection = dropConnection;
        _status = status;

        _hostIp = config.HostOverlayIp;
        _discoveryPort = config.DiscoveryUdpPort?.ToString() ?? string.Empty;
        _broadcastDestination = string.IsNullOrWhiteSpace(config.BroadcastDestination) ? "255.255.255.255" : config.BroadcastDestination;
        _payloadHex = config.PayloadPrefixHex;
        _preserveBroadcast = config.PreserveOriginalBroadcast;

        RefreshAdaptersCommand = new RelayCommand(_ => RefreshAdapters());
        StartStopCommand = new RelayCommand(_ => _ = ToggleAsync());
        ForceKillCommand = new RelayCommand(_ => ForceKill(), _ => BridgeRunning || HelperLost);
        LaunchBlurCommand = new RelayCommand(_ => LaunchBlur(), _ => File.Exists(config.BlurExePath));
        PollStatusCommand = new RelayCommand(_ => _ = PollOnceAsync(), _ => BridgeRunning);
        DetectCommand = new RelayCommand(_ => _ = SniffAsync(replies: false), _ => !BridgeRunning && !SniffRunning);
        ListenRepliesCommand = new RelayCommand(_ => _ = SniffAsync(replies: true), _ => !BridgeRunning && !SniffRunning);
        StopSniffCommand = new RelayCommand(_ => _ = StopSniffAsync(), _ => SniffRunning);
        ApplySniffCommand = new RelayCommand(_ => ApplySniff(), _ => SniffCandidate > 0 && !SniffRunning && !BridgeRunning);
        _blurWatcher.Exited += OnBlurExited;
        RefreshAdapters();
        RaisePreflight();
        AttachBlur();
        _ui = SynchronizationContext.Current;
        // Re-scan every 5s: games started after the app (or relaunched after
        // a crash) get picked up without any clicks.
        _rescanTimer = new Timer(_ =>
        {
            try
            {
                if (_disposed || _blurWatcher.Watching)
                {
                    return;
                }

                if (_ui is not null)
                {
                    _ui.Post(__ => AttachBlur(silent: true), null);
                }
                else
                {
                    AttachBlur(silent: true);
                }
            }
            catch
            {
                // background rescan must never throw
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _initialized = true;
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
            DiscoveryPort = _config.DiscoveryUdpPort?.ToString() ?? string.Empty;
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
        LaunchBlurCommand.RaiseCanExecuteChanged();
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

    private void RefreshAdapters()
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
            Message = "Adapter enumeration failed: " + ex.Message;
            AppLog.Error("Adapter enumeration failed: " + ex.Message);
        }
    }

    private async Task ToggleAsync()
    {
        if (BridgeRunning)
        {
            await StopAsync().ConfigureAwait(true);
        }
        else
        {
            await StartAsync().ConfigureAwait(true);
        }
    }

    private bool ValidateInputs(out int port, out string error)
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

    /// <summary>Launches the helper on demand (UAC) and connects the pipe.</summary>
    private async Task<HelperIpcClient> EnsureHelperAsync()
    {
        if (_launcher.IsRunning && !_helperSessionOk)
        {
            // Stale helper from an earlier session (our token is dead):
            // replace it instead of talking to a wall.
            AppLog.Warn("Helper process alive but session dead — restarting it.");
            _launcher.Kill();
            _dropConnection();
        }

        if (!_launcher.IsRunning)
        {
            _launcher.Launch(watchdogSec: 15);
            _dropConnection(); // new process = new pipe/token: never reuse a stale client
        }

        return await _connect(() => _launcher.IsRunning, CancellationToken.None, 60).ConfigureAwait(false);
    }

    public async Task StartAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Message = string.Empty;
        HelperLost = false;
        if (!ValidateInputs(out int port, out string error))
        {
            Message = error;
            AppLog.Warn("Bridge start rejected (validation): " + error);
            _busy = false; // release before returning: the try/finally below never runs
            return;
        }

        // Persist validated inputs.
        _config.HostOverlayIp = HostIp.Trim();
        _config.DiscoveryUdpPort = port;
        _config.BroadcastDestination = BroadcastDestination.Trim();
        _config.PayloadPrefixHex = PayloadHex?.Trim() ?? string.Empty;
        _config.PreserveOriginalBroadcast = PreserveBroadcast;
        if (SelectedAdapter is not null)
        {
            _config.SelectedAdapterIfIndex = SelectedAdapter.IfIndex;
        }

        _onChanged();

        // Attach to an already-running Blur so auto-stop works regardless
        // of how the game was started.
        AttachBlur(silent: true);

        // Pre-flight: show the exact narrow filter before elevation.
        string filter;
        try
        {
            filter = WinDivertFilterBuilder.Build(new WinDivertFilterBuilder.FilterInput(
                port, _config.BroadcastDestination, SelectedAdapter?.DirectedBroadcast));
            ActiveFilter = filter;
        }
        catch (Exception ex)
        {
            Message = "Filter validation failed: " + ex.Message;
            AppLog.Warn("Filter validation failed: " + ex.Message);
            _busy = false; // release before returning: the try/finally below never runs
            return;
        }

        AppLog.Info($"Bridge start: host={_config.HostOverlayIp}:{port} bcast={_config.BroadcastDestination} " +
                    $"preserve={_config.PreserveOriginalBroadcast} filter='{filter}'");

        try
        {
            Message = "Requesting elevation for the helper (UAC)…";
            var ipc = await EnsureHelperAsync().ConfigureAwait(true);

            var start = new IpcStartRequest
            {
                Token = _launcher.Token,
                HostOverlayIp = _config.HostOverlayIp,
                DiscoveryUdpPort = port,
                BroadcastDestination = _config.BroadcastDestination,
                PayloadPrefixHex = _config.PayloadPrefixHex,
                PreserveOriginalBroadcast = _config.PreserveOriginalBroadcast,
                RateLimitPerSecond = _config.RateLimitPerSecond,
                RateLimitBurst = BlurLinkConstants.DefaultRateLimitBurst,
                AdapterIfIndex = _config.SelectedAdapterIfIndex,
            };
            var response = await ipc.SendAsync(start, CancellationToken.None).ConfigureAwait(true);
            using var doc = JsonDocument.Parse(response);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == IpcMessageTypes.Error)
            {
                Message = "Helper rejected start: " + doc.RootElement.GetProperty("message").GetString();
                AppLog.Warn("Helper rejected start: " + Message);
                _launcher.Kill();
                return;
            }

            _pollFailures = 0;
            _helperSessionOk = true; // the start reply proves an authenticated session
            SniffRunning = false;
            BridgeRunning = true;
            Message = "Bridge running. Open Blur and search its LAN games list.";
            _status($"Bridge active — {HostIp.Trim()}:{port} via filter: {filter}");
            AppLog.Info("Bridge running.");
            Raise(nameof(RouteWarning));
            Raise(nameof(RouteDetails));
            // Merged: reply listening rides along automatically (separate
            // observe-only handle). Reuse the session we just authenticated:
            // re-entering EnsureHelperAsync here would restart the very
            // helper we just started, silently leaving the bridge stopped.
            await StartReplyListenAsync(ipc).ConfigureAwait(true);
            StartPolling();
        }
        catch (OperationCanceledException)
        {
            Message = "Elevation cancelled — bridge not started (helper exits automatically).";
            AppLog.Info("Bridge start cancelled at UAC.");
        }
        catch (Exception ex)
        {
            Message = "Start failed: " + ex.Message;
            AppLog.Error("Bridge start failed: " + ex.Message);
            _launcher.Kill();
            _dropConnection();
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task StopAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            _pollCts?.Cancel();
            if (_launcher.IsRunning)
            {
                var ipc = await _connect(() => _launcher.IsRunning, CancellationToken.None, 6).ConfigureAwait(true);
                await ipc.SendAsync(new IpcSimpleCommand { Type = IpcMessageTypes.Stop, Token = _launcher.Token }, CancellationToken.None).ConfigureAwait(true);
                await ipc.SendAsync(new IpcSimpleCommand { Type = IpcMessageTypes.Shutdown, Token = _launcher.Token }, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Bridge stop IPC failed (forcing kill): " + ex.Message);
        }
        finally
        {
            _dropConnection();
            var exited = await WaitForHelperExitAsync(3000).ConfigureAwait(true);
            _launcher.Kill();
            _dropConnection();
            BridgeRunning = false;
            HelperLost = false;
            SniffRunning = false;
            SniffCandidate = 0;
            RepliesSummary = string.Empty;
            _helperSessionOk = false;
            Message = exited
                ? "Bridge stopped — helper exited cleanly. No interception remains."
                : "Bridge stopped — helper did not exit, force-killed. No interception remains.";
            _status("Bridge stopped.");
            AppLog.Info("Bridge stopped. Final counters: " + Counters);
            _busy = false;
        }
    }

    /// <summary>
    /// Waits for the helper to exit on its own (after shutdown). Awaited, not
    /// blocked: Stop also runs from the UI thread (Stop button, window close),
    /// and a Thread.Sleep loop here froze the window for up to the full timeout
    /// whenever the helper was slow to exit.
    /// </summary>
    private async Task<bool> WaitForHelperExitAsync(int millis)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(millis);
            while (DateTime.UtcNow < deadline)
            {
                if (!_launcher.IsRunning)
                {
                    return true;
                }

                await Task.Delay(100).ConfigureAwait(true);
            }

            return !_launcher.IsRunning;
        }
        catch
        {
            return false;
        }
    }

    public void ForceKill()
    {
        _pollCts?.Cancel();
        _launcher.Kill();
        _dropConnection();
        BridgeRunning = false;
        HelperLost = false;
        SniffRunning = false;
        SniffCandidate = 0;
        RepliesSummary = string.Empty;
        _helperSessionOk = false;
        Message = "Helper force-killed. No interception remains (verify counters stay frozen).";
        _status("Helper force-killed.");
        AppLog.Warn("Helper force-killed by user.");
    }

    private void StartPolling()
    {
        if (_pollCts is { IsCancellationRequested: false })
        {
            return;
        }

        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && (BridgeRunning || SniffRunning))
        {
            try
            {
                await Task.Delay(1500, ct).ConfigureAwait(true);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            await PollOnceAsync().ConfigureAwait(true);
        }
    }

    private async Task PollOnceAsync()
    {
        try
        {
            var ipc = await _connect(() => _launcher.IsRunning, CancellationToken.None, 6).ConfigureAwait(true);
            var response = await ipc.SendAsync(
                new IpcSimpleCommand { Type = IpcMessageTypes.GetStatus, Token = _launcher.Token },
                CancellationToken.None).ConfigureAwait(true);
            var status = JsonSerializer.Deserialize<IpcStatusResponse>(response);
            if (status is not null && status.Type == IpcMessageTypes.Status)
            {
                _pollFailures = 0;
                HelperLost = false;
                _helperSessionOk = true;
                Counters = $"captured={status.Captured} forwarded={status.Forwarded} reinjected={status.Reinjected} dropped={status.Dropped} errors={status.InjectionErrors} frags={status.FragmentsRejected} dedup={status.DedupSkipped}" +
                           (status.WatchdogSec > 0 ? $" wd={status.WatchdogSec}s" : string.Empty);
                if (!string.IsNullOrWhiteSpace(status.Filter))
                {
                    ActiveFilter = status.Filter;
                }

                if (!string.IsNullOrWhiteSpace(status.LastError))
                {
                    Message = "Helper: " + status.LastError;
                }

                RecentEvents.Clear();
                foreach (var e in status.Recent.TakeLast(BlurLinkConstants.MaxRecentPackets))
                {
                    RecentEvents.Add($"{e.TimestampUtc:HH:mm:ss} {e.SrcIp}:{e.SrcPort} -> {e.OrigDstIp}:{e.OrigDstPort} fwd {e.ForwardedDstIp} [{e.Action}]");
                }

                UpdateSniffFromStatus(status);
            }
        }
        catch (Exception ex)
        {
            // Poll failures are non-fatal until they persist: then the helper
            // is assumed lost (its watchdog will also exit it). Surface it.
            if (++_pollFailures >= 3 && (BridgeRunning || SniffRunning))
            {
                HelperLost = true;
                SniffRunning = false;
                Message = "Lost connection to the helper (" + ex.Message +
                          "). It should exit by itself (watchdog); use Force Kill if its counters move.";
                AppLog.Error("Helper connection lost: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Research actions, both SNIFF-only (nothing intercepted):
    /// - discover: which broadcast UDP ports Blur uses (finds the port);
    /// - replies: what answers on the discovery port (finds the host reply).
    /// Bounded time + packet cap, metadata only.
    /// </summary>
    public async Task SniffAsync(bool replies)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _sniffReplies = replies;
        Raise(nameof(ShowApplySniff));
        SniffStatus = string.Empty;
        SniffCandidate = 0;
        RepliesSummary = string.Empty;
        if (SniffRunning)
        {
            _busy = false;
            return;
        }

        if (!replies && BridgeRunning)
        {
            // Discovery sniffing would stop the bridge helper-side; say so.
            SniffStatus = "Stop the bridge first — discovery listening takes the session.";
            _busy = false;
            return;
        }

        int replyPort = 0;
        List<string> broadcasts = new();
        if (!replies)
        {
            // Listen on global + directed broadcast, plus a user-configured
            // destination (e.g. multicast) when it differs.
            broadcasts.Add("255.255.255.255");
            var directed = SelectedAdapter?.DirectedBroadcast;
            if (!string.IsNullOrWhiteSpace(directed) && directed != broadcasts[0])
            {
                broadcasts.Add(directed);
            }

            var configured = BroadcastDestination?.Trim();
            if (!string.IsNullOrWhiteSpace(configured)
                && !broadcasts.Contains(configured, StringComparer.Ordinal))
            {
                try
                {
                    BroadcastValidator.ValidateOrThrow(configured, SelectedAdapter?.DirectedBroadcast);
                    broadcasts.Add(configured);
                }
                catch
                {
                    // invalid custom entry: fall back to the standard two
                }
            }
        }
        else if (!int.TryParse(DiscoveryPort?.Trim(), out replyPort) || replyPort is < 1 or > 65535)
        {
            SniffStatus = "Set the discovery port first (Detect it above), then listen for replies.";
            _busy = false;
            return;
        }

        string filter;
        try
        {
            filter = SniffFilterBuilder.Build(broadcasts, SelectedAdapter?.DirectedBroadcast,
                replies ? "in" : "out", replyPort);
            SniffFilterBuilder.ValidateSniffParams(15, 200);
        }
        catch (Exception ex)
        {
            SniffStatus = "Cannot listen: " + ex.Message;
            AppLog.Warn("Sniff rejected (validation): " + ex.Message);
            _busy = false;
            return;
        }

        AppLog.Info($"Sniff start ({(replies ? "replies" : "discover")}, 15s): filter='{filter}'.");
        try
        {
            if (!await SendSniffRequestAsync(broadcasts, replies ? "in" : "out", replyPort).ConfigureAwait(true))
            {
                _busy = false;
                return;
            }

            SniffRunning = true;
            SniffStatus = replies
                ? $"Listening for replies on port {replyPort}… refresh Blur's LAN list with the bridge running."
                : "Listening… open Blur and refresh its LAN list.";
            StartPolling();
        }
        catch (OperationCanceledException)
        {
            SniffStatus = "Elevation cancelled — listener not started.";
        }
        catch (Exception ex)
        {
            SniffStatus = "Listen failed: " + ex.Message;
            AppLog.Error("Sniff failed: " + ex.Message);
            _launcher.Kill();
            _dropConnection();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Starts reply listening without taking the busy lock (called from
    /// StartAsync, which already holds it). Never fails the bridge.
    /// </summary>
    private async Task StartReplyListenAsync(HelperIpcClient liveClient)
    {
        _sniffReplies = true;
        Raise(nameof(ShowApplySniff));
        if (!int.TryParse(DiscoveryPort?.Trim(), out int replyPort) || replyPort is < 1 or > 65535)
        {
            return; // port validated at Start; defensive only
        }

        try
        {
            if (await SendSniffRequestAsync(new List<string>(), "in", replyPort, liveClient).ConfigureAwait(true))
            {
                SniffRunning = true;
                RepliesSummary = "Watching for the host's answer…";
            }
        }
        catch (Exception ex)
        {
            RepliesSummary = "Reply watch failed to start: " + ex.Message;
            AppLog.Warn("Auto reply-listen failed: " + ex.Message);
        }
    }

    /// <summary>Sends a sniff command; returns false when the helper refuses.</summary>
    private async Task<bool> SendSniffRequestAsync(
        List<string> broadcasts, string direction, int port, HelperIpcClient? liveClient = null)
    {
        // Callers that already hold an authenticated session (StartAsync) pass
        // it in, so the sniff never re-runs the launch/relaunch decision.
        var ipc = liveClient ?? await EnsureHelperAsync().ConfigureAwait(true);
        var req = new IpcSniffRequest
        {
            Token = _launcher.Token,
            Broadcasts = broadcasts,
            Direction = direction,
            Port = port,
            DurationSec = 15,
            MaxPackets = 200,
        };
        var response = await ipc.SendAsync(req, CancellationToken.None).ConfigureAwait(true);
        using var doc = JsonDocument.Parse(response);
        if (doc.RootElement.GetProperty("type").GetString() == IpcMessageTypes.Error)
        {
            var msg = doc.RootElement.GetProperty("message").GetString();
            if (direction == "in")
            {
                RepliesSummary = "Reply watch rejected: " + msg;
            }
            else
            {
                SniffStatus = "Listener rejected: " + msg;
            }

            AppLog.Warn("Sniff rejected: " + msg);
            if (liveClient is null)
            {
                // We launched this helper only for the sniff and nothing is
                // bridged, so an elevated stray must not be left behind.
                _launcher.Kill();
                _dropConnection();
            }

            // Riding an existing session (the automatic reply-listen after a
            // successful Start): the listener is observe-only and optional, so
            // its rejection must never tear down the live bridge it rode on.
            return false;
        }

        return true;
    }

    public async Task StopSniffAsync()
    {
        try
        {
            if (_launcher.IsRunning)
            {
                var ipc = await _connect(() => _launcher.IsRunning, CancellationToken.None, 6).ConfigureAwait(true);
                // stop_sniff halts listening only — the bridge (if running) survives.
                await ipc.SendAsync(new IpcSimpleCommand { Type = IpcMessageTypes.StopSniff, Token = _launcher.Token }, CancellationToken.None).ConfigureAwait(true);
                // Verify it actually stopped instead of assuming.
                var response = await ipc.SendAsync(
                    new IpcSimpleCommand { Type = IpcMessageTypes.GetStatus, Token = _launcher.Token },
                    CancellationToken.None).ConfigureAwait(true);
                var status = JsonSerializer.Deserialize<IpcStatusResponse>(response);
                if (status is { SniffActive: true })
                {
                    SniffStatus = "Listener would not stop — use Force Kill on the main card.";
                    AppLog.Error("Sniff stop refused by helper; still active.");
                    return;
                }

                UpdateSniffFromStatus(status ?? new IpcStatusResponse());
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Sniff stop IPC failed: " + ex.Message);
        }
        finally
        {
            SniffRunning = false;
        }
    }

    private void ApplySniff()
    {
        if (SniffCandidate < 1 || SniffRunning || BridgeRunning)
        {
            return;
        }

        DiscoveryPort = SniffCandidate.ToString();
        _config.DiscoveryUdpPort = SniffCandidate;
        _onChanged();
        Message = $"Discovery port set to {SniffCandidate}. You can start the bridge now.";
        AppLog.Info($"Sniff candidate applied as discovery port: {SniffCandidate}.");
        Raise(nameof(RouteWarning));
        Raise(nameof(RouteDetails));
    }

    private void UpdateSniffFromStatus(IpcStatusResponse status)
    {
        if (!SniffRunning)
        {
            return;
        }

        if (status.SniffActive)
        {
            var total = status.SniffResults.Sum(r => r.Count);
            SniffStatus = _sniffReplies
                ? $"Listening for replies… {total} packet(s) so far."
                : total == 0
                    ? "Listening… open Blur and refresh its LAN list."
                    : $"Listening… {total} broadcast packet(s) so far.";
            if (_sniffReplies && total > 0)
            {
                RepliesSummary = FormatReplySummary(status.SniffResults, _config.HostOverlayIp);
            }

            return;
        }

        // Finished (duration elapsed or packet cap reached).
        SniffRunning = false;
        var results = status.SniffResults.OrderByDescending(r => r.Count).ToArray();
        var totalCount = results.Sum(r => r.Count);
        if (_sniffReplies)
        {
            FinishReplyListen(results, totalCount);
            return;
        }

        if (results.Length == 0)
        {
            SniffCandidate = 0;
            SniffStatus = "Nothing heard. Was Blur's LAN list refreshed during those 15s? Retry while refreshing.";
            AppLog.Info("Sniff finished: no broadcast UDP observed.");
            return;
        }

        var top = results[0];
        SniffCandidate = top.Port;
        var detail = string.Join(", ", results.Take(3).Select(r => $"{r.Port} × {r.Count}"));
        SniffStatus = results.Length == 1 || top.Count >= 3
            ? $"Blur is broadcasting on port {top.Port} ({top.Count} of {totalCount} packets). Press Apply."
            : $"Candidates: {detail}. Top pick is {top.Port} — verify with one more listen, then Apply.";
        AppLog.Info($"Sniff finished: {detail}. Candidate={top.Port}.");
    }

    private void FinishReplyListen(SniffPortCount[] results, long totalCount)
    {
        SniffCandidate = 0;
        if (results.Length == 0)
        {
            SniffStatus = "No replies arrived. Either the host never answered (firewall? lobby closed? " +
                          "wrong host IP?) or answers can't route back. Check Settings → Ping host.";
            RepliesSummary = "No replies arrived.";
            AppLog.Info("Reply listen finished: nothing arrived.");
            return;
        }

        var detail = string.Join(", ", results.Take(3).Select(r =>
            string.IsNullOrWhiteSpace(r.SrcIp) ? $"{r.Port} × {r.Count}" : $"{r.SrcIp}:{r.Port} × {r.Count}"));
        var fromHost = results.Any(r =>
            string.Equals(r.SrcIp, _config.HostOverlayIp, StringComparison.Ordinal));
        SniffStatus = fromHost
            ? $"Host answered ({detail}). If the lobby still doesn't show, Blur ignored the reply — see troubleshooting."
            : $"Something answered ({detail}) — but NOT your host IP. Check the host address.";
        RepliesSummary = FormatReplySummary(results, _config.HostOverlayIp);
        AppLog.Info($"Reply listen finished: {detail} (total {totalCount}).");
    }

    private void LaunchBlur()
    {
        AttachBlur(silent: true);
        try
        {
            var gameDir = Path.GetDirectoryName(_config.BlurExePath) ?? string.Empty;
            var psi = new ProcessStartInfo(_config.BlurExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = gameDir, // game folder, not ours — else instant crash
                Arguments = _config.BlurArgs,
            };
            AppLog.Info($"Launching Blur: exe='{_config.BlurExePath}' workdir='{gameDir}' args='{_config.BlurArgs}'.");
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                AppLog.Info($"Blur launched from Join tab (pid={proc.Id}).");
                try
                {
                    _blurWatcher.Watch(proc);
                    BlurStatus = $"Blur: running (launched, pid {proc.Id}).";
                    Message = StopWhenBlurExits
                        ? "Blur launched — bridge will stop automatically when Blur exits."
                        : "Blur launched (auto-stop disabled).";
                }
                catch (Exception ex)
                {
                    Message = "Blur launched (could not attach exit watcher).";
                    AppLog.Warn("Blur exit watcher attach failed: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Message = "Launch failed: " + ex.Message;
            AppLog.Error($"Blur launch failed: {ex.GetType().Name}: {ex.Message} (exe='{_config.BlurExePath}').");
        }
    }

    private void OnBlurExited()
    {
        var exitInfo = _blurWatcher.HasExitInfo
            ? $" after {_blurWatcher.LastRunDuration.TotalSeconds:F0}s (code {_blurWatcher.LastExitCode})"
            : string.Empty;
        AppLog.Info($"Blur exited{exitInfo}.");
        BlurStatus = "Blur: not running.";
        if (!StopWhenBlurExits || !BridgeRunning)
        {
            Message = "Blur exited" + exitInfo + ".";
            return;
        }

        Message = "Blur exited — stopping bridge automatically.";
        AppLog.Info("Blur exited; auto-stopping bridge.");
        _ = StopAsync();
    }

    /// <summary>
    /// Attaches the exit watcher to already-running Blur (user started it
    /// themselves), so auto-stop and status work without launching via us.
    /// </summary>
    public void AttachBlur(bool silent = false)
    {
        try
        {
            if (_blurWatcher.Watching)
            {
                BlurStatus = $"Blur: running (watched, pid {_blurWatcher.PrimaryPid}).";
                return;
            }

            if (_blurWatcher.AttachToRunning("Blur", _config.BlurExePath))
            {
                var pid = _blurWatcher.PrimaryPid;
                BlurStatus = pid == 0 ? "Blur: running (attached)." : $"Blur: running (attached, pid {pid}).";
                AppLog.Info("Attached to running Blur instance" + (pid == 0 ? "." : $" (pid {pid})."));
            }
            else if (!silent)
            {
                BlurStatus = "Blur: not running.";
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Blur attach scan failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _rescanTimer.Dispose();
        }
        catch
        {
            // best effort
        }

        _blurWatcher.Dispose();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
    }
}
