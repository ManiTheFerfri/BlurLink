using BlurLink.Contracts;

namespace BlurLink.Core.Session;

public enum SessionMode { None, Sniff, Bridge, Host }

public enum SessionPhase { Idle, Starting, Running, Stopping, Failed }

public sealed record SessionCapabilities(
    bool HelperPresent,
    bool OverlayAddressKnown,
    bool AdapterSelected,
    bool GameRunning)
{
    public static readonly SessionCapabilities Unknown = new(false, false, false, false);
}

/// <summary>Helper counters, copied one for one. Never payloads, never game data.</summary>
public sealed record SessionCounters(
    long Captured,
    long Forwarded,
    long Reinjected,
    long Dropped,
    long DedupSkipped,
    long AnnouncementsSent,
    long HostForwardsHeard,
    long HostRepliesForwarded,
    long HostBroadcastReplies,
    long HostAmbiguousReplies)
{
    public static readonly SessionCounters Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static SessionCounters From(IpcStatusResponse s) => new(
        s.Captured, s.Forwarded, s.Reinjected, s.Dropped, s.DedupSkipped,
        s.AnnouncementsSent, s.HostForwardsHeard, s.HostRepliesForwarded,
        s.HostBroadcastReplies, s.HostAmbiguousReplies);
}

/// <summary>A reason a session cannot run, with the fix the user can act on.</summary>
public sealed record BlockingReason(string Code, string Text, string Fix);

/// <summary>One player the host accepted, as the Host tab needs it.</summary>
public sealed record PlayerFact(
    string OverlayIp,
    string LanIp,
    int BlurSourcePort,
    long ForwardsHeard,
    bool InFilter);

public sealed record SessionState(
    SessionMode Mode,
    SessionPhase Phase,
    SessionCounters Counters,
    IReadOnlyList<PlayerFact> Players,
    IReadOnlyList<BlockingReason> BlockingReasons,
    string LastError)
{
    public static readonly SessionState Initial = new(
        SessionMode.None, SessionPhase.Idle, SessionCounters.Empty,
        Array.Empty<PlayerFact>(), Array.Empty<BlockingReason>(), string.Empty);

    public bool IsRunning => Phase == SessionPhase.Running;

    public bool IsBusy => Phase is SessionPhase.Starting or SessionPhase.Stopping;

    public static SessionState FromStatus(
        IpcStatusResponse status,
        SessionPhase phase,
        IReadOnlyList<BlockingReason> blocking) => new(
        status.HostActive ? SessionMode.Host
            : status.SniffActive ? SessionMode.Sniff
            : status.Active ? SessionMode.Bridge
            : SessionMode.None,
        phase,
        SessionCounters.From(status),
        status.HostPlayers
            .Select(p => new PlayerFact(p.OverlayIp, p.LanIp, p.BlurSourcePort, p.ForwardsHeard, p.InFilter))
            .ToList(),
        blocking,
        status.LastError);
}
