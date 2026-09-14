using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Core.Session;
using BlurLink.Platform;

namespace BlurLink.Shell.ViewModels;

/// <summary>
/// Join tab part 3 of 3: coordinator, story, session, game. Port of the
/// matching <c>BlurLink.Desktop</c> <c>JoinViewModel</c> members, verbatim
/// unless noted. Field validation lives on <see cref="BridgeSettingsViewModel"/>;
/// sniffing on <see cref="DiscoverySniffViewModel"/>.
/// </summary>
public sealed class JoinSessionViewModel : ShellViewModelBase, IDisposable
{
    private readonly BlurLinkConfig _config;
    private readonly Action _onChanged;
    private readonly IHelperProcess _launcher;
    private readonly Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> _connect;
    private readonly Action _dropConnection;
    private readonly Action<string> _status;
    private readonly BridgeSettingsViewModel _settings;
    private readonly DiscoverySniffViewModel _sniff;
    private readonly BlurProcessWatcher _blurWatcher = new();
    private readonly SynchronizationContext? _ui;
    private readonly Timer _rescanTimer;
    private bool _disposed;
    private int _pollFailures;
    private bool _helperSessionOk; // true after an authenticated status reply
    private bool _busy; // an explicit Start/Stop operation is in flight (sniff ops guard on the sniff part)
    private readonly SessionCoordinator _coordinator;
    private readonly HelperIpcClient? _ownedChannelClient; // placeholder behind the coordinator's PipeHelperChannel; never touches the pipe

    public BridgeSettingsViewModel Settings => _settings;
    public DiscoverySniffViewModel Sniff => _sniff;

    public ObservableCollection<string> RecentEvents { get; } = new();

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
                // Detect/Listen/Apply exclude a live bridge.
                _sniff.RefreshCommands();
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

    private string _message = string.Empty;
    public string Message { get => _message; set => Set(ref _message, value); }

    private string _blurStatus = "Blur: not running.";
    public string BlurStatus { get => _blurStatus; set => Set(ref _blurStatus, value); }

    private string _counters = "captured=0 forwarded=0 reinjected=0 dropped=0 errors=0 frags=0 dedup=0";
    public string Counters { get => _counters; set => Set(ref _counters, value); }

    /// <summary>User-facing session story, derived from the coordinator state. Replaces the hand-built poll sentences.</summary>
    public SessionStory Story => SessionStoryTable.Describe(_coordinator.State);

    /// <summary>Raw helper counters for the Counters disclosure. Single source: the coordinator state.</summary>
    public string RawCounters =>
        $"captured={_coordinator.State.Counters.Captured} forwarded={_coordinator.State.Counters.Forwarded} "
        + $"dropped={_coordinator.State.Counters.Dropped} dedupSkipped={_coordinator.State.Counters.DedupSkipped}";

    private string _activeFilter = string.Empty;
    public string ActiveFilter { get => _activeFilter; set => Set(ref _activeFilter, value); }

    public string StartStopText => BridgeRunning ? "Stop Bridge" : "Start Bridge";

    public bool ShowStart => !BridgeRunning;

    public bool ShowForceKill => BridgeRunning || HelperLost;

    /// <summary>Forwarded coordinator transitions (MainViewModel and Task 11 subscribe).</summary>
    public event Action<SessionState>? StateChanged;

    public RelayCommand StartStopCommand { get; }
    public RelayCommand ForceKillCommand { get; }
    public RelayCommand LaunchBlurCommand { get; }
    public RelayCommand PollStatusCommand { get; }

    private CancellationTokenSource? _pollCts;

    public JoinSessionViewModel(
        BlurLinkConfig config,
        Action onChanged,
        IHelperProcess launcher,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect,
        Action dropConnection,
        Action<string> status,
        BridgeSettingsViewModel settings,
        DiscoverySniffViewModel sniff)
        : this(config, onChanged, launcher, connect, dropConnection, status, settings, sniff,
            NewCoordinator(out HelperIpcClient owned), owned)
    {
    }

    /// <summary>Builds the production coordinator over a placeholder channel. The coordinator is used
    /// as a state folder via ApplyStatus/ApplyError (the poll does its own IO through _connect), so the
    /// placeholder client never touches the pipe; it only lets the ctor's initial refresh report
    /// helper-not-running instead of a stale counter line.</summary>
    private static SessionCoordinator NewCoordinator(out HelperIpcClient owned)
    {
        owned = new HelperIpcClient();
        return new SessionCoordinator(new PipeHelperChannel(owned));
    }

    private JoinSessionViewModel(
        BlurLinkConfig config,
        Action onChanged,
        IHelperProcess launcher,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect,
        Action dropConnection,
        Action<string> status,
        BridgeSettingsViewModel settings,
        DiscoverySniffViewModel sniff,
        SessionCoordinator coordinator,
        HelperIpcClient? ownedChannelClient)
    {
        _coordinator = coordinator;
        _ownedChannelClient = ownedChannelClient;
        _config = config;
        _onChanged = onChanged;
        _launcher = launcher;
        _connect = connect;
        _dropConnection = dropConnection;
        _status = status;
        _settings = settings;
        _sniff = sniff;

        // Start stays disabled until preflight passes (and no sniff holds the session);
        // Stop is always available while running.
        StartStopCommand = new RelayCommand(
            _ => _ = ToggleAsync(),
            _ => BridgeRunning || (_settings.PreflightReady && !_sniff.SniffRunning));
        ForceKillCommand = new RelayCommand(_ => ForceKill(), _ => BridgeRunning || HelperLost);
        LaunchBlurCommand = new RelayCommand(_ => LaunchBlur(), _ => File.Exists(config.BlurExePath));
        PollStatusCommand = new RelayCommand(_ => _ = PollOnceAsync(), _ => BridgeRunning);
        _blurWatcher.Exited += OnBlurExited;
        AttachBlur();
        _sniff.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoverySniffViewModel.SniffRunning))
            {
                if (_sniff.SniffRunning)
                {
                    StartPolling();
                }

                StartStopCommand.RaiseCanExecuteChanged();
            }
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BridgeSettingsViewModel.PreflightReady))
            {
                StartStopCommand.RaiseCanExecuteChanged();
            }
        };
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
        _coordinator.StateChanged += state =>
        {
            OnCoordinatorStateChanged(state);
            StateChanged?.Invoke(state);
        };
        UpdateCoordinatorCapabilities();
        _ = RefreshStoryAsync(); // initial helper-not-running story; completes synchronously when disconnected
    }

    /// <summary>Test seam: drive the tab from a fake helper instead of a real one.</summary>
    public static JoinSessionViewModel ForTests(IHelperChannel channel)
    {
        var config = BlurLinkConfig.CreateDefault();
        JoinSessionViewModel? session = null;
        var settings = new BridgeSettingsViewModel(config, () => { }, () => session?.UpdateCoordinatorCapabilities());
        var coordinator = new SessionCoordinator(channel);
        var launcher = new HelperLauncher();
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect =
            (_, _, _) => throw new InvalidOperationException("ForTests has no pipe.");
        var sniff = new DiscoverySniffViewModel(
            launcher, connect, () => { }, _ => { }, settings,
            () => session?.BridgeRunning == true,
            portText => session?.ApplySniffedPort(portText));
        session = new JoinSessionViewModel(
            config,
            () => { },
            launcher,
            connect,
            () => { },
            _ => { },
            settings,
            sniff,
            coordinator,
            ownedChannelClient: null);
        // Set after construction: the ctor syncs capabilities from the environment.
        coordinator.Capabilities = ReadyForTests();
        return session;
    }

    public static SessionCapabilities ReadyForTests() =>
        new(HelperPresent: true, OverlayAddressKnown: true, AdapterSelected: true, GameRunning: true);

    /// <summary>One helper status through the coordinator, then rebind the view.</summary>
    public void ApplyStatusForTests(IpcStatusResponse status)
    {
        _coordinator.ApplyStatus(status);
        Raise(nameof(Story));
        Raise(nameof(RawCounters));
    }

    /// <summary>Keeps the coordinator's blocking reasons truthful as adapters, helper files and Blur come and go.</summary>
    public void UpdateCoordinatorCapabilities()
    {
        _coordinator.Capabilities = new SessionCapabilities(
            HelperPresent: HelperLauncher.HelperAvailable,
            OverlayAddressKnown: _settings.SelectedAdapter is not null,
            AdapterSelected: _settings.SelectedAdapter is not null,
            GameRunning: _blurWatcher.Watching);
    }

    private void OnCoordinatorStateChanged(SessionState state)
    {
        Raise(nameof(Story));
        Raise(nameof(RawCounters));
    }

    /// <summary>Initial story at startup (helper-not-running when disconnected). Never throws.</summary>
    private async Task RefreshStoryAsync()
    {
        try
        {
            await _coordinator.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            // Initial story must never fail construction.
        }

        Raise(nameof(Story));
        Raise(nameof(RawCounters));
        Counters = RawCounters;
    }

    /// <summary>Error-envelope guard mirroring SessionCoordinator's (private there).</summary>
    private static bool IsHelperError(string raw, out string message)
    {
        message = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("type", out var type)
                && type.GetString() == IpcMessageTypes.Error)
            {
                if (doc.RootElement.TryGetProperty("message", out var msg))
                {
                    message = msg.GetString() ?? string.Empty;
                }

                return true;
            }
        }
        catch (JsonException)
        {
            // Not an error envelope; treated as a status below.
        }

        return false;
    }

    /// <summary>
    /// Re-reads every bound field from the config (after Settings Import/
    /// Reset). The settings part reloads the fields; this part refreshes the
    /// Blur launch enablement that also depends on config.
    /// </summary>
    public void RefreshFromConfig()
    {
        _settings.RefreshFromConfig();
        LaunchBlurCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Applies a detected discovery port: the settings part persists it, this
    /// part sets the confirmation Message (WPF <c>ApplySniff</c>, second half).
    /// </summary>
    public void ApplySniffedPort(string portText)
    {
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
        {
            return;
        }

        _settings.ApplySniffedPort(port);
        Message = $"Discovery port set to {port}. You can start the bridge now.";
        AppLog.Info($"Sniff candidate applied as discovery port: {port}.");
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
        if (!_settings.ValidateInputs(out int port, out string error))
        {
            Message = error;
            AppLog.Warn("Bridge start rejected (validation): " + error);
            _busy = false; // release before returning: the try/finally below never runs
            return;
        }

        // Persist validated inputs.
        _config.HostOverlayIp = _settings.HostIp.Trim();
        _config.DiscoveryUdpPort = port;
        _config.BroadcastDestination = _settings.BroadcastDestination.Trim();
        _config.PayloadPrefixHex = _settings.PayloadHex?.Trim() ?? string.Empty;
        _config.PreserveOriginalBroadcast = _settings.PreserveBroadcast;
        if (_settings.SelectedAdapter is not null)
        {
            _config.SelectedAdapterIfIndex = _settings.SelectedAdapter.IfIndex;
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
                port, _config.BroadcastDestination, _settings.SelectedAdapter?.DirectedBroadcast));
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
            _sniff.SniffRunning = false;
            BridgeRunning = true;
            Message = "Bridge running. Open Blur and search its LAN games list.";
            _status($"Bridge active — {_settings.HostIp.Trim()}:{port} via filter: {filter}");
            AppLog.Info("Bridge running.");
            // Merged: reply listening rides along automatically (separate
            // observe-only handle). Reuse the session we just authenticated:
            // re-entering EnsureHelperAsync here would restart the very
            // helper we just started, silently leaving the bridge stopped.
            await _sniff.StartReplyListenAsync(ipc).ConfigureAwait(true);
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
            _sniff.SniffRunning = false;
            _sniff.SniffCandidate = 0;
            _sniff.RepliesSummary = string.Empty;
            _helperSessionOk = false;
            Message = exited
                ? "Bridge stopped — helper exited cleanly. No interception remains."
                : "Bridge stopped — helper did not exit, force-killed. No interception remains.";
            _status("Bridge stopped.");
            AppLog.Info("Bridge stopped. Final counters: " + Counters);
            // The poll loop exits with the bridge, so push the idle state here:
            // otherwise the story would keep claiming the stopped session.
            _coordinator.ApplyStatus(new IpcStatusResponse());
            Raise(nameof(Story));
            Raise(nameof(RawCounters));
            Counters = RawCounters;
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
        _sniff.SniffRunning = false;
        _sniff.SniffCandidate = 0;
        _sniff.RepliesSummary = string.Empty;
        _helperSessionOk = false;
        // Same idle push as StopAsync: no poll will run to update the story.
        _coordinator.ApplyStatus(new IpcStatusResponse());
        Raise(nameof(Story));
        Raise(nameof(RawCounters));
        Counters = RawCounters;
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
        while (!ct.IsCancellationRequested && (BridgeRunning || _sniff.SniffRunning))
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
            // Same IsError guard StartAsync uses: an error envelope on poll becomes
            // Failed with the helper message rather than Idle.
            if (IsHelperError(response, out var helperError))
            {
                _pollFailures = 0;
                HelperLost = false;
                _coordinator.ApplyError(helperError);
                Raise(nameof(Story));
                Raise(nameof(RawCounters));
                Counters = RawCounters;
                return;
            }

            var status = JsonSerializer.Deserialize<IpcStatusResponse>(response);
            if (status is not null && status.Type == IpcMessageTypes.Status)
            {
                _pollFailures = 0;
                HelperLost = false;
                _helperSessionOk = true;
                // Single source for status text: the coordinator state behind
                // Story/RawCounters. Retired: the hand-built Counters line and the
                // "Helper: ..." Message branch (the story carries both now).
                _coordinator.ApplyStatus(status);
                Raise(nameof(Story));
                Raise(nameof(RawCounters));
                Counters = RawCounters;
                if (!string.IsNullOrWhiteSpace(status.Filter))
                {
                    ActiveFilter = status.Filter;
                }

                RecentEvents.Clear();
                foreach (var e in status.Recent.TakeLast(BlurLinkConstants.MaxRecentPackets))
                {
                    RecentEvents.Add($"{e.TimestampUtc:HH:mm:ss} {e.SrcIp}:{e.SrcPort} -> {e.OrigDstIp}:{e.OrigDstPort} fwd {e.ForwardedDstIp} [{e.Action}]");
                }

                _sniff.UpdateSniffFromStatus(status);
            }
        }
        catch (Exception ex)
        {
            // Poll failures are non-fatal until they persist: then the helper
            // is assumed lost (its watchdog will also exit it). Surface it.
            if (++_pollFailures >= 3 && (BridgeRunning || _sniff.SniffRunning))
            {
                HelperLost = true;
                _sniff.SniffRunning = false;
                Message = "Lost connection to the helper (" + ex.Message +
                          "). It should exit by itself (watchdog); use Force Kill if its counters move.";
                AppLog.Error("Helper connection lost: " + ex.Message);
                // Phase truthfulness: the story must not keep claiming a live session.
                _coordinator.ApplyError("Lost connection to the helper: " + ex.Message);
                Raise(nameof(Story));
                Raise(nameof(RawCounters));
                Counters = RawCounters;
            }
        }
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
                    UpdateCoordinatorCapabilities();
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
        UpdateCoordinatorCapabilities();
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
                UpdateCoordinatorCapabilities();
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

        UpdateCoordinatorCapabilities();
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
        try
        {
            _ownedChannelClient?.Dispose();
        }
        catch
        {
            // best effort
        }
    }
}
