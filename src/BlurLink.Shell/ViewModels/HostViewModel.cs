using System.Collections.ObjectModel;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Core.Session;
using BlurLink.Platform;

namespace BlurLink.Shell.ViewModels;

/// <summary>One row in the host-mode player list.</summary>
public sealed class HostPlayerRow
{
    public string OverlayIp { get; init; } = string.Empty;
    public string LanIp { get; init; } = string.Empty;
    public int BlurSourcePort { get; init; }
    public long ForwardsHeard { get; init; }
    public long RepliesForwarded { get; init; }

    /// <summary>False once the player stops announcing; the row stays visible.</summary>
    public bool InFilter { get; init; }

    public string State => InFilter ? "active" : "quiet";

    public string Summary => $"{OverlayIp}   ->   replies to {LanIp}:{BlurSourcePort}";
}

/// <summary>
/// Host mode tab: run the helper in its host role so the host's Blur replies get
/// forwarded to each player's overlay address.
///
/// Port of the matching <c>BlurLink.Desktop</c> <c>HostViewModel</c> members,
/// verbatim unless noted. The hand-built <c>FormatStatus</c> sentence builder is
/// deliberately retired (not ported): the status sentence comes only from
/// <see cref="SessionStoryTable"/> via this VM's own coordinator (spec §7.2).
/// <see cref="StatusText"/> is kept as the last-action line ("Revoked x.",
/// "Host mode stopped.") — not the session sentence; the story banner carries that.
/// </summary>
public sealed class HostViewModel : ShellViewModelBase, IDisposable
{
    private readonly BlurLinkConfig _config;
    private readonly Action _onChanged;
    private readonly IHelperProcess _launcher;
    private readonly Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> _connect;
    private readonly Action _dropConnection;
    private readonly Action<string> _status;
    private readonly SessionCoordinator _coordinator;
    private readonly HelperIpcClient? _ownedChannelClient; // placeholder behind the coordinator's PipeHelperChannel; never touches the pipe
    private bool _disposed;
    private bool _suppressConfigWrite;

    public ObservableCollection<AdapterInfo> Adapters { get; } = new();
    public ObservableCollection<HostPlayerRow> Players { get; } = new();

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
                    _onChanged();
                }

                RaisePreflight();
                UpdateCoordinatorCapabilities();
            }
        }
    }

    private string _discoveryPort = string.Empty;
    public string DiscoveryPort
    {
        get => _discoveryPort;
        set { if (Set(ref _discoveryPort, value)) { RaisePreflight(); } }
    }

    private bool _autoAccept = true;
    public bool AutoAccept
    {
        get => _autoAccept;
        set
        {
            if (Set(ref _autoAccept, value))
            {
                _config.HostAutoAccept = value;
                _onChanged();
                AppLog.Info("Host mode: auto-accept " + (value ? "on" : "off") +
                            " (players are still listed and can be revoked).");
            }
        }
    }

    private bool _hostRunning;
    public bool HostRunning
    {
        get => _hostRunning;
        private set
        {
            if (Set(ref _hostRunning, value))
            {
                Raise(nameof(ShowStartHost));
            }
        }
    }

    public bool ShowStartHost => !HostRunning;

    /// <summary>Compact one-line counter summary for the status card and footer.</summary>
    public string Counters => $"heard={ForwardsHeard} forwarded={RepliesForwarded}";

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private long _forwardsHeard;
    public long ForwardsHeard
    {
        get => _forwardsHeard;
        private set
        {
            if (Set(ref _forwardsHeard, value))
            {
                Raise(nameof(Counters));
            }
        }
    }

    private long _repliesForwarded;
    public long RepliesForwarded
    {
        get => _repliesForwarded;
        private set
        {
            if (Set(ref _repliesForwarded, value))
            {
                Raise(nameof(Counters));
            }
        }
    }

    /// <summary>True when no player has introduced themselves yet.</summary>
    public bool ShowNoPlayers => Players.Count == 0;

    private string _activeFilter = string.Empty;
    public string ActiveFilter { get => _activeFilter; private set => Set(ref _activeFilter, value); }

    private string _statusText = "Host mode is off. Start it before your friends join.";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>User-facing session story, derived from the coordinator state. Replaces the retired FormatStatus sentences.</summary>
    public SessionStory Story => SessionStoryTable.Describe(_coordinator.State);

    /// <summary>Current coordinator snapshot for the Diagnostics sections (Task 12).</summary>
    public SessionState CoordinatorState => _coordinator.State;

    /// <summary>Forwarded coordinator transitions (MainViewModel and Task 11 subscribe).</summary>
    public event Action<SessionState>? StateChanged;

    public RelayCommand RefreshAdaptersCommand { get; }
    public RelayCommand StartHostCommand { get; }
    public RelayCommand StopHostCommand { get; }
    public RelayCommand RevokePlayerCommand { get; }

    public HostViewModel(
        BlurLinkConfig config,
        Action onChanged,
        IHelperProcess launcher,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect,
        Action dropConnection,
        Action<string> status)
        : this(config, onChanged, launcher, connect, dropConnection, status,
            NewCoordinator(out HelperIpcClient owned), owned)
    {
    }

    /// <summary>Builds the production coordinator over a placeholder channel. The coordinator is used
    /// as a state folder via ApplyStatus/ApplyError (Start/Stop/Revoke do their own IO through _connect),
    /// so the placeholder client never touches the pipe; it only lets the ctor's initial refresh report
    /// helper-not-running instead of a stale counter line.</summary>
    private static SessionCoordinator NewCoordinator(out HelperIpcClient owned)
    {
        owned = new HelperIpcClient();
        return new SessionCoordinator(new PipeHelperChannel(owned));
    }

    private HostViewModel(
        BlurLinkConfig config,
        Action onChanged,
        IHelperProcess launcher,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect,
        Action dropConnection,
        Action<string> status,
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

        _suppressConfigWrite = true;
        _autoAccept = config.HostAutoAccept;
        _discoveryPort = config.DiscoveryUdpPort?.ToString() ?? string.Empty;
        _suppressConfigWrite = false;

        RefreshAdaptersCommand = new RelayCommand(_ => RefreshAdapters());
        StartHostCommand = new RelayCommand(async _ => await StartHostAsync().ConfigureAwait(true));
        StopHostCommand = new RelayCommand(async _ => await StopHostAsync().ConfigureAwait(true));
        RevokePlayerCommand = new RelayCommand(async p => await RevokeAsync(p as HostPlayerRow).ConfigureAwait(true));

        RefreshAdapters();
        RaisePreflight();
        _coordinator.StateChanged += state =>
        {
            OnCoordinatorStateChanged(state);
            StateChanged?.Invoke(state);
        };
        UpdateCoordinatorCapabilities();
        _ = RefreshStoryAsync(); // initial helper-not-running story; completes synchronously when disconnected
    }

    /// <summary>Test seam: drive the tab from a fake helper instead of a real one.</summary>
    public static HostViewModel ForTests(IHelperChannel channel)
    {
        var config = BlurLinkConfig.CreateDefault();
        var coordinator = new SessionCoordinator(channel);
        var launcher = new HelperLauncher();
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect =
            (_, _, _) => throw new InvalidOperationException("ForTests has no pipe.");
        var vm = new HostViewModel(
            config,
            () => { },
            launcher,
            connect,
            () => { },
            _ => { },
            coordinator,
            ownedChannelClient: null);
        // Set after construction: the ctor syncs capabilities from the environment.
        coordinator.Capabilities = ReadyForTests();
        return vm;
    }

    public static SessionCapabilities ReadyForTests() =>
        new(HelperPresent: true, OverlayAddressKnown: true, AdapterSelected: true, GameRunning: true);

    /// <summary>One helper status through the coordinator, then rebind the view.</summary>
    public void ApplyStatusForTests(IpcStatusResponse status) => ApplyStatus(status);

    /// <summary>Keeps the coordinator's blocking reasons truthful as adapters and helper files come and go.</summary>
    public void UpdateCoordinatorCapabilities()
    {
        _coordinator.Capabilities = new SessionCapabilities(
            HelperPresent: HelperLauncher.HelperAvailable,
            OverlayAddressKnown: _selectedAdapter is not null,
            AdapterSelected: _selectedAdapter is not null,
            GameRunning: true);
    }

    private void OnCoordinatorStateChanged(SessionState state)
    {
        Raise(nameof(Story));
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

    /// <summary>Reloads fields from the config after an import/reset.</summary>
    public void RefreshFromConfig()
    {
        _suppressConfigWrite = true;
        try
        {
            _autoAccept = _config.HostAutoAccept;
            _discoveryPort = _config.DiscoveryUdpPort?.ToString() ?? string.Empty;
            var ifIndex = _config.SelectedAdapterIfIndex;
            _selectedAdapter = Adapters.FirstOrDefault(a => a.IfIndex == ifIndex);
        }
        finally
        {
            _suppressConfigWrite = false;
        }

        Raise(nameof(AutoAccept));
        Raise(nameof(DiscoveryPort));
        Raise(nameof(SelectedAdapter));
        RaisePreflight();
        UpdateCoordinatorCapabilities();
        Raise(nameof(Story));
    }

    public void RefreshAdapters()
    {
        try
        {
            var current = _config.SelectedAdapterIfIndex;
            Adapters.Clear();
            foreach (var a in AdapterEnumerator.Enumerate())
            {
                Adapters.Add(a);
            }

            _suppressConfigWrite = true;
            _selectedAdapter = Adapters.FirstOrDefault(a => a.IfIndex == current) ?? Adapters.FirstOrDefault();
            if (_selectedAdapter is not null)
            {
                _config.SelectedAdapterIfIndex = _selectedAdapter.IfIndex;
            }

            _suppressConfigWrite = false;
            Raise(nameof(SelectedAdapter));
            RaisePreflight();
            UpdateCoordinatorCapabilities();
            Raise(nameof(Story));
        }
        catch (Exception ex)
        {
            AppLog.Error("Host mode: adapter enumeration failed: " + ex.Message);
            _status("Could not list adapters: " + ex.Message);
        }
    }

    /// <summary>Advice shown under the Start button.</summary>
    private void RaisePreflight()
    {
        Preflight = BuildPreflight();
        Raise(nameof(Preflight));
        StartHostCommand.RaiseCanExecuteChanged();
    }

    private string _preflight = string.Empty;
    public string Preflight { get => _preflight; private set => Set(ref _preflight, value); }

    private string BuildPreflight()
    {
        if (SelectedAdapter is null)
        {
            return "Pick your overlay adapter first.";
        }

        if (!int.TryParse(DiscoveryPort?.Trim(), out var port) || port is < 1 or > 65535)
        {
            return "Enter the verified discovery port (Join -> Advanced -> Detect discovers it).";
        }

        if (port == BlurLinkConstants.HostAnnounceUdpPort)
        {
            return $"Port {port} is BlurLink's own introduction port; use the discovery port instead.";
        }

        return $"Ready: watching port {port} on {SelectedAdapter.FriendlyName} for player introductions.";
    }

    private int ParsedPort =>
        int.TryParse(DiscoveryPort?.Trim(), out var p) ? p : 0;

    public async Task StartHostAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (ParsedPort is < 1 or > 65535)
        {
            const string msg = "Host mode needs the verified discovery port before it can start.";
            StatusText = msg;
            _status(msg);
            return;
        }

        if (SelectedAdapter is null)
        {
            const string msg = "Host mode needs your overlay adapter selected.";
            StatusText = msg;
            _status(msg);
            return;
        }

        IsBusy = true;
        try
        {
            if (!_launcher.IsRunning)
            {
                _launcher.Launch();
            }

            var client = await _connect(() => _launcher.IsRunning, CancellationToken.None, 30)
                .ConfigureAwait(true);

            var request = new IpcStartHostRequest
            {
                Token = _launcher.Token,
                DiscoveryUdpPort = ParsedPort,
                AdapterIfIndex = SelectedAdapter.IfIndex,
            };
            var response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(true);
            ApplyResponse(response);
            if (HostRunning)
            {
                MainViewModel.RememberHost(_config); // Task 11: per-profile host memory at start time
            }
        }
        catch (Exception ex)
        {
            _dropConnection();
            const string prefix = "Host mode could not start: ";
            StatusText = prefix + ex.Message;
            _status(StatusText);
            AppLog.Error("Host mode start failed: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StopHostAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (_launcher.IsRunning)
            {
                var client = await _connect(() => _launcher.IsRunning, CancellationToken.None, 10)
                    .ConfigureAwait(true);
                var response = await client.SendAsync(
                    new IpcSimpleCommand { Type = IpcMessageTypes.Stop, Token = _launcher.Token },
                    CancellationToken.None).ConfigureAwait(true);
                ApplyResponse(response);
            }

            HostRunning = false;
            Players.Clear();
            StatusText = "Host mode stopped.";
            _status(StatusText);
        }
        catch (Exception ex)
        {
            _dropConnection();
            StatusText = "Host mode could not stop cleanly: " + ex.Message;
            _status(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RevokeAsync(HostPlayerRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var client = await _connect(() => _launcher.IsRunning, CancellationToken.None, 10)
                .ConfigureAwait(true);
            var response = await client.SendAsync(
                new IpcRevokeHostPlayerRequest { Token = _launcher.Token, OverlayIp = row.OverlayIp },
                CancellationToken.None).ConfigureAwait(true);
            ApplyResponse(response);
            StatusText = $"Revoked {row.OverlayIp}.";
            _status(StatusText);
        }
        catch (Exception ex)
        {
            _dropConnection();
            StatusText = "Could not revoke " + row.OverlayIp + ": " + ex.Message;
            _status(StatusText);
        }
    }

    private void ApplyResponse(string response)
    {
        if (IsHelperError(response, out var helperError))
        {
            _coordinator.ApplyError(helperError);
            Raise(nameof(Story));
            StatusText = "Helper refused: " + (string.IsNullOrWhiteSpace(helperError) ? "unknown error" : helperError);
            _status(StatusText);
            return;
        }

        var status = JsonSerializer.Deserialize<IpcStatusResponse>(response);
        if (status is not null)
        {
            ApplyStatus(status);
        }
    }

    /// <summary>
    /// Applies a helper status reply: folds it through the coordinator for the
    /// story, then updates the player roster and counters. Pure apart from
    /// raising change notifications. The retired FormatStatus sentence is gone:
    /// StatusText keeps only the last action; the story banner is the sentence.
    /// </summary>
    public void ApplyStatus(IpcStatusResponse status)
    {
        _coordinator.ApplyStatus(status);
        Raise(nameof(Story));
        HostRunning = status.HostActive;
        ForwardsHeard = status.HostForwardsHeard;
        RepliesForwarded = status.HostRepliesForwarded;
        ActiveFilter = status.HostFilter;

        Raise(nameof(ShowNoPlayers));
        Players.Clear();
        foreach (var p in status.HostPlayers ?? new List<HostPlayerStatus>())
        {
            Players.Add(new HostPlayerRow
            {
                OverlayIp = p.OverlayIp,
                LanIp = p.LanIp,
                BlurSourcePort = p.BlurSourcePort,
                ForwardsHeard = p.ForwardsHeard,
                RepliesForwarded = p.RepliesForwarded,
                InFilter = p.InFilter,
            });
        }

        Raise(nameof(ShowNoPlayers));
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
            _ownedChannelClient?.Dispose();
        }
        catch
        {
            // best effort
        }
    }
}
