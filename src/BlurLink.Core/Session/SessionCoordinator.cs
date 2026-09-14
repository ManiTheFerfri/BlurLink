using System.Text.Json;
using BlurLink.Contracts;

namespace BlurLink.Core.Session;

/// <summary>
/// Owns the transition between helper replies and <see cref="SessionState"/>.
/// It is the only place that decides what the session is doing, so a phase can
/// never be inferred differently in two views.
/// </summary>
public sealed class SessionCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHelperChannel _channel;
    private SessionPhase _phase = SessionPhase.Idle;
    private SessionMode _mode = SessionMode.None;

    public SessionCoordinator(IHelperChannel channel) => _channel = channel;

    public SessionState State { get; private set; } = SessionState.Initial;

    public event Action<SessionState>? StateChanged;

    private SessionCapabilities _capabilities = SessionCapabilities.Unknown;

    /// <summary>Set by the app once it knows what exists on this machine.</summary>
    public SessionCapabilities Capabilities
    {
        get => _capabilities;
        set
        {
            _capabilities = value;
            BlockingReasons = ResolveBlockingReasons(value);
        }
    }

    /// <summary>Always derived from <see cref="Capabilities"/>; never set directly.</summary>
    public IReadOnlyList<BlockingReason> BlockingReasons { get; private set; } =
        ResolveBlockingReasons(SessionCapabilities.Unknown);

    /// <summary>Capability-derived reasons, evaluated once per set of capabilities.</summary>
    public static IReadOnlyList<BlockingReason> ResolveBlockingReasons(SessionCapabilities c)
    {
        var reasons = new List<BlockingReason>();
        if (!c.HelperPresent)
        {
            reasons.Add(new BlockingReason(
                "helper-missing",
                "The helper is not available on this machine.",
                "Build it with scripts/build.ps1, or use the portable build that embeds it."));
        }
        if (!c.OverlayAddressKnown)
        {
            reasons.Add(new BlockingReason(
                "overlay-address-missing",
                "Your overlay address is not set, so a host has no address to reply to.",
                "Pick your overlay adapter, or enter the address on the Join tab."));
        }
        return reasons;
    }

    public async Task<SessionState> RefreshAsync(CancellationToken ct)
    {
        if (!_channel.IsConnected)
        {
            _phase = SessionPhase.Idle;
            _mode = SessionMode.None;
            return Publish(SessionState.Initial with
            {
                BlockingReasons = BlockingReasons.Concat(new[]
                {
                    new BlockingReason(
                        "helper-not-running",
                        "The helper is not running, so there is no session.",
                        "Start a session to launch it (Windows will ask for Administrator)."),
                }).ToList(),
            });
        }

        string raw;
        try
        {
            raw = await _channel.SendAsync(new IpcSimpleCommand { Type = IpcMessageTypes.GetStatus }, ct);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        IpcStatusResponse? status;
        try
        {
            status = JsonSerializer.Deserialize<IpcStatusResponse>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fail($"The helper's status could not be parsed ({ex.Message}).");
        }

        if (status is null)
        {
            return Fail("The helper returned an empty status.");
        }

        return ApplyStatus(status);
    }

    /// <summary>
    /// Fold a status reply into the state. The poll loop and the tests both use
    /// this; it never talks to the helper itself, so the caller can pass a status
    /// it already parsed instead of paying for a second round trip.
    /// </summary>
    public SessionState ApplyStatus(IpcStatusResponse status)
    {
        _mode = status.HostActive ? SessionMode.Host
            : status.SniffActive ? SessionMode.Sniff
            : status.Active ? SessionMode.Bridge
            : SessionMode.None;

        // The helper is the authority on what is live: a session that exists is
        // Running, one that does not is Idle — regardless of what phase we were in
        // locally. A stop in flight keeps its phase until the helper confirms, so
        // the UI cannot claim to be idle while a teardown is still happening.
        if (_phase != SessionPhase.Stopping)
        {
            _phase = _mode == SessionMode.None ? SessionPhase.Idle : SessionPhase.Running;
        }

        return Publish(SessionState.FromStatus(status, _phase, BlockingReasons));
    }

    public async Task<SessionState> StartAsync(object startRequest, SessionMode mode, CancellationToken ct)
    {
        _mode = mode;
        _phase = SessionPhase.Starting;
        Publish(SessionState.Initial with { Mode = mode, Phase = _phase, BlockingReasons = BlockingReasons });

        string raw;
        try
        {
            raw = await _channel.SendAsync(startRequest, ct);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        if (IsError(raw, out var message))
        {
            return Fail(message);
        }

        _phase = SessionPhase.Running;
        return await RefreshAsync(ct);
    }

    public async Task<SessionState> StopAsync(CancellationToken ct)
    {
        _phase = SessionPhase.Stopping;
        try
        {
            await _channel.SendAsync(new IpcSimpleCommand { Type = IpcMessageTypes.Stop }, ct);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        _phase = SessionPhase.Idle;
        _mode = SessionMode.None;
        return await RefreshAsync(ct);
    }

    private static bool IsError(string raw, out string message)
    {
        message = string.Empty;
        try
        {
            if (JsonSerializer.Deserialize<IpcErrorResponse>(raw, JsonOptions) is { } error
                && string.Equals(error.Type, IpcMessageTypes.Error, StringComparison.Ordinal))
            {
                message = error.Message;
                return true;
            }
        }
        catch (JsonException)
        {
            // Not an error envelope; treated as a status below.
        }
        return false;
    }

    private SessionState Fail(string message)
    {
        _phase = SessionPhase.Failed;
        return Publish(SessionState.Initial with
        {
            Mode = _mode,
            Phase = SessionPhase.Failed,
            BlockingReasons = BlockingReasons,
            LastError = message,
        });
    }

    private SessionState Publish(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
        return state;
    }
}
