using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Core.Validation;
using BlurLink.Platform;

namespace BlurLink.Shell.ViewModels;

/// <summary>
/// Join tab part 2 of 3: discovery sniffing / reply listening. Never touches
/// the settings VM directly — the detected port flows out through
/// <c>applyPort</c>. Port of the matching WPF <c>JoinViewModel</c> members, verbatim unless noted.
/// </summary>
public sealed class DiscoverySniffViewModel : ShellViewModelBase
{
    private readonly IHelperProcess _launcher;
    private readonly Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> _connect;
    private readonly Action _dropConnection;
    private readonly BridgeSettingsViewModel _settings;
    private readonly Func<bool> _isSessionRunning;
    private readonly Action<string> _applyPort;
    private bool _busy; // a sniff operation is in flight (session Start/Stop guard lives on the session part)

    public DiscoverySniffViewModel(
        IHelperProcess launcher,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect,
        Action dropConnection,
        Action<string> status,
        BridgeSettingsViewModel settings,
        Func<bool> isSessionRunning,
        Action<string> applyPort)
    {
        // Sniff progress surfaces through SniffStatus; the shell status line
        // stays with session transitions (WPF parity), so there is nothing to
        // store from status.
        _ = status;
        _launcher = launcher;
        _connect = connect;
        _dropConnection = dropConnection;
        _settings = settings;
        _isSessionRunning = isSessionRunning;
        _applyPort = applyPort;

        DetectCommand = new RelayCommand(_ => _ = SniffAsync(replies: false), _ => !_isSessionRunning() && !SniffRunning);
        ListenRepliesCommand = new RelayCommand(_ => _ = SniffAsync(replies: true), _ => !_isSessionRunning() && !SniffRunning);
        StopSniffCommand = new RelayCommand(_ => _ = StopSniffAsync(), _ => SniffRunning);
        ApplySniffCommand = new RelayCommand(_ => ApplySniff(), _ => SniffCandidate > 0 && !SniffRunning && !_isSessionRunning());
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

    public RelayCommand DetectCommand { get; }
    public RelayCommand ListenRepliesCommand { get; }
    public RelayCommand StopSniffCommand { get; }
    public RelayCommand ApplySniffCommand { get; }

    /// <summary>Refreshes Detect/Listen/Apply enablement when the session part
    /// reports a BridgeRunning change (those commands exclude a live bridge).</summary>
    public void RefreshCommands()
    {
        DetectCommand.RaiseCanExecuteChanged();
        ListenRepliesCommand.RaiseCanExecuteChanged();
        StopSniffCommand.RaiseCanExecuteChanged();
        ApplySniffCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Launches the helper on demand (UAC) and connects the pipe. Private copy
    /// of the session part's method: both parts receive the same launcher
    /// trio so each stays self-sufficient for helper IO.
    /// </summary>
    private async Task<HelperIpcClient> EnsureHelperAsync()
    {
        if (_launcher.IsRunning)
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

        if (!replies && _isSessionRunning())
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
            var directed = _settings.SelectedAdapter?.DirectedBroadcast;
            if (!string.IsNullOrWhiteSpace(directed) && directed != broadcasts[0])
            {
                broadcasts.Add(directed);
            }

            var configured = _settings.BroadcastDestination?.Trim();
            if (!string.IsNullOrWhiteSpace(configured)
                && !broadcasts.Contains(configured, StringComparer.Ordinal))
            {
                try
                {
                    BroadcastValidator.ValidateOrThrow(configured, _settings.SelectedAdapter?.DirectedBroadcast);
                    broadcasts.Add(configured);
                }
                catch
                {
                    // invalid custom entry: fall back to the standard two
                }
            }
        }
        else if (!int.TryParse(_settings.DiscoveryPort?.Trim(), out replyPort) || replyPort is < 1 or > 65535)
        {
            SniffStatus = "Set the discovery port first (Detect it above), then listen for replies.";
            _busy = false;
            return;
        }

        string filter;
        try
        {
            filter = SniffFilterBuilder.Build(broadcasts, _settings.SelectedAdapter?.DirectedBroadcast,
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
            // The session part starts its poll loop off the SniffRunning
            // change (its handler also refreshes Start enablement).
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
    /// Starts reply listening without taking the busy lock (called from the
    /// session part's StartAsync, which already holds it). Never fails the bridge.
    /// </summary>
    public async Task StartReplyListenAsync(HelperIpcClient liveClient)
    {
        _sniffReplies = true;
        Raise(nameof(ShowApplySniff));
        if (!int.TryParse(_settings.DiscoveryPort?.Trim(), out int replyPort) || replyPort is < 1 or > 65535)
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
        if (SniffCandidate < 1 || SniffRunning || _isSessionRunning())
        {
            return;
        }

        // The port flows out through the facade wiring (session persists it
        // and sets Message); this part never touches settings directly.
        _applyPort(SniffCandidate.ToString());
    }

    public void UpdateSniffFromStatus(IpcStatusResponse status)
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
                RepliesSummary = FormatReplySummary(status.SniffResults, _settings.HostIp);
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
            string.Equals(r.SrcIp, _settings.HostIp, StringComparison.Ordinal));
        SniffStatus = fromHost
            ? $"Host answered ({detail}). If the lobby still doesn't show, Blur ignored the reply — see troubleshooting."
            : $"Something answered ({detail}) — but NOT your host IP. Check the host address.";
        RepliesSummary = FormatReplySummary(results, _settings.HostIp);
        AppLog.Info($"Reply listen finished: {detail} (total {totalCount}).");
    }
}
