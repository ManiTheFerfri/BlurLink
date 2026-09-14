using System.Collections.ObjectModel;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Core.Validation;
using BlurLink.Desktop.Services;

namespace BlurLink.Desktop.ViewModels;

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
/// The framing in <see cref="FormatStatus"/> is deliberately explicit about the
/// two ways host mode can be silently doing nothing — no forwards arriving at
/// all, or forwards arriving but no replies captured — because both look
/// identical to the user otherwise ("it's on and nothing is happening").
/// </summary>
public sealed class HostViewModel : ViewModelBase, IDisposable
{
    private readonly BlurLinkConfig _config;
    private readonly Action _onChanged;
    private readonly IHelperProcess _launcher;
    private readonly Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> _connect;
    private readonly Action _dropConnection;
    private readonly Action<string> _status;
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

    public string ActiveFilter { get; private set; } = string.Empty;

    private string _statusText = "Host mode is off. Start it before your friends join.";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

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
    {
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
        using var doc = JsonDocument.Parse(response);
        if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
            typeEl.GetString() == IpcMessageTypes.Error)
        {
            var detail = doc.RootElement.TryGetProperty("message", out var msgEl)
                ? msgEl.GetString()
                : "unknown error";
            StatusText = "Helper refused: " + detail;
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
    /// Applies a helper status reply. Pure apart from raising change
    /// notifications, so the framing below is directly testable.
    /// </summary>
    public void ApplyStatus(IpcStatusResponse status)
    {
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
        StatusText = FormatStatus(status);
    }

    /// <summary>
    /// Turns counters into something a human can act on. Order matters: the
    /// prerequisite (are forwards arriving at all?) comes before anything about
    /// replies, because if forwards are not arriving nothing else can be fixed
    /// on this machine.
    /// </summary>
    public static string FormatStatus(IpcStatusResponse s)
    {
        if (!s.HostActive)
        {
            return "Host mode is off. Start it before your friends join.";
        }

        if (s.HostForwardsHeard == 0)
        {
            return "Host mode is running, but the host is not receiving discovery from any player " +
                   "yet. Ask them to start their bridge and refresh Blur's LAN list; if it stays " +
                   "at zero, the overlay is not carrying their packets and host mode cannot help.";
        }

        var lines = new List<string>
        {
            $"Heard {s.HostForwardsHeard} discovery packet(s) from players; " +
            $"forwarded {s.HostRepliesForwarded} repl{(s.HostRepliesForwarded == 1 ? "y" : "ies")}.",
        };

        if (s.HostRepliesForwarded == 0)
        {
            lines.Add("No replies captured yet: refresh Blur's LAN list on the host. If it stays at " +
                      "zero while forwards are arriving, the host's Blur may be replying from a " +
                      "different port than expected.");
        }

        if (s.HostBroadcastReplies > 0)
        {
            lines.Add($"{s.HostBroadcastReplies} repl{(s.HostBroadcastReplies == 1 ? "y was" : "ies were")} " +
                      "addressed to a broadcast address. That shape is not verified yet, so those " +
                      "were not forwarded (the host's own LAN is untouched).");
        }

        if (s.HostAmbiguousReplies > 0)
        {
            lines.Add($"{s.HostAmbiguousReplies} repl{(s.HostAmbiguousReplies == 1 ? "y" : "ies")} " +
                      "matched two players at once and were refused rather than sent to the wrong person.");
        }

        if (s.HostUnmatchedReplies > 0)
        {
            lines.Add($"{s.HostUnmatchedReplies} reply(ies) went to an address or port that is not a " +
                      "known player. The address and port shown are the likely mismatch.");
        }

        if (s.HostAnnounceRejected > 0)
        {
            lines.Add($"{s.HostAnnounceRejected} introduction(s) were malformed and ignored.");
        }

        if (s.HostCollisions > 0)
        {
            lines.Add($"{s.HostCollisions} player(s) were refused because their address and port " +
                      "matched someone already connected.");
        }

        if (s.HostFilterReopens > 0)
        {
            lines.Add($"Filter rebuilt {s.HostFilterReopens} time(s) as players joined or left.");
        }

        if (s.HostInjectionErrors > 0)
        {
            lines.Add($"{s.HostInjectionErrors} reply(ies) could not be sent.");
        }

        return string.Join(" ", lines);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
