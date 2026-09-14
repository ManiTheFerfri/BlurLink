namespace BlurLink.Core.Session;

public enum StorySeverity { Neutral, Progress, Attention }

public sealed record SessionStory(
    string Code,
    string Headline,
    string Detail,
    string SuggestedAction,
    StorySeverity Severity);

/// <summary>
/// The only place user-visible session sentences are written. Rules are ordered;
/// the first match wins, so the table stays deterministic and testable.
/// </summary>
public static class SessionStoryTable
{
    public static SessionStory Describe(SessionState s)
    {
        if (s.BlockingReasons.Count > 0)
        {
            var first = s.BlockingReasons[0];
            return new SessionStory("blocked", "Not ready yet", first.Text, first.Fix, StorySeverity.Attention);
        }

        return s.Phase switch
        {
            SessionPhase.Failed => new SessionStory(
                "failed", "The session stopped with an error",
                string.IsNullOrWhiteSpace(s.LastError) ? "No error detail was reported." : s.LastError,
                "Open Diagnostics for the helper log, then start again.", StorySeverity.Attention),

            SessionPhase.Starting => new SessionStory(
                "starting", "Starting…",
                "Approve the Windows prompt if one appears — the helper needs Administrator.",
                string.Empty, StorySeverity.Progress),

            SessionPhase.Stopping => new SessionStory(
                "stopping", "Stopping…", "Waiting for the helper to close its session.",
                string.Empty, StorySeverity.Progress),

            SessionPhase.Running when s.Mode == SessionMode.Sniff => new SessionStory(
                "sniff", "Listening for broadcasts",
                "Refresh the game's LAN list now so the listener can hear the discovery packet.",
                string.Empty, StorySeverity.Progress),

            SessionPhase.Running when s.Mode == SessionMode.Bridge => BridgeStory(s),

            SessionPhase.Running when s.Mode == SessionMode.Host => HostStory(s),

            _ => new SessionStory(
                "idle", "Nothing is running",
                "No session is active, so no packets are being captured or forwarded.",
                "Start a session, or run the first-run check.", StorySeverity.Neutral),
        };
    }

    private static SessionStory BridgeStory(SessionState s) => s.Counters.Captured switch
    {
        0 => new SessionStory(
            "bridge-idle", "Nothing captured yet",
            "Blur's discovery packet has not been seen on this machine.",
            "Open Blur's LAN browser and refresh the server list.", StorySeverity.Neutral),

        _ when s.Counters.Forwarded == 0 => new SessionStory(
            "bridge-seen-only", "Blur is being seen, but nothing is forwarded yet",
            "Packets reached the capture filter but none matched the discovery port or the payload gate.",
            "Check the discovery port in the profile — a wrong port is the usual cause.", StorySeverity.Attention),

        _ => new SessionStory(
            "bridge-forwarding", "You are reaching the host",
            "Your query is being forwarded over the overlay. If the lobby does not appear, the host is not sending replies back — that is what Host mode fixes.",
            "If the lobby stays empty, ask the host to start Host mode.", StorySeverity.Progress),
    };

    private static SessionStory HostStory(SessionState s)
    {
        // Order matters: the per-player test must come before the reply test, or
        // "heard but nothing forwarded back" is unreachable whenever a player is
        // in the roster with a zero count.
        if (s.Players.Count == 0 && s.Counters.HostForwardsHeard == 0)
        {
            return new SessionStory(
                "host-waiting", "Waiting for a player",
                "No player has announced itself yet. This is normal until someone searches for your game.",
                "Tell your friend to open Blur's LAN list.", StorySeverity.Neutral);
        }

        if (s.Players.Any(p => p.ForwardsHeard == 0))
        {
            return new SessionStory(
                "host-no-forward", "A player has announced itself, but no query has arrived",
                "The player has not searched yet. A read of 0 here does not mean they are blocked.",
                "Ask them to refresh Blur's LAN list.", StorySeverity.Neutral);
        }

        if (s.Counters.HostRepliesForwarded == 0)
        {
            return new SessionStory(
                "host-heard-no-reply", "The player's query arrived, but no reply has been forwarded",
                "Your Blur has not answered that query yet, so there is nothing to forward.",
                "Make sure a LAN lobby is actually open in Blur.", StorySeverity.Attention);
        }

        return new SessionStory(
            "host-forwarding", "Replies are being sent back over the overlay",
            "Your Blur has answered, and the reply has been copied to the player's overlay address.",
            string.Empty, StorySeverity.Progress);
    }
}
