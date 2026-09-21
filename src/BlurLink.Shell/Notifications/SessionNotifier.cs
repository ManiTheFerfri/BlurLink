namespace BlurLink.Shell.Notifications;

/// <summary>Pure transition table: previous story code + next story code → a
/// `Notice` for the center, or null when nothing worth interrupting happened.
/// Fires once per transition — steady states never re-notify. Returns the
/// center's own record type so MainViewModel pushes it without mapping.</summary>
public static class SessionNotifier
{
    public static Notice? Watch(string prevCode, string nextCode)
    {
        if (string.Equals(prevCode, nextCode, StringComparison.Ordinal))
        {
            return null;
        }

        return nextCode switch
        {
            "bridge-forwarding" => new Notice("Lobby may appear",
                "You are reaching the host. If the lobby does not appear, the host may need Host mode.",
                NoticeSeverity.Progress, DateTime.UtcNow),
            "host-forwarding" => new Notice("Replies on their way",
                "Your Blur answered, and replies are being sent back over the overlay.",
                NoticeSeverity.Progress, DateTime.UtcNow),
            "failed" => new Notice("Session stopped with an error",
                "Open Diagnostics for the helper log, then start again.",
                NoticeSeverity.Error, DateTime.UtcNow),
            "blocked" => new Notice("Not ready yet",
                "A requirement is missing — see the session story for the fix.",
                NoticeSeverity.Warning, DateTime.UtcNow),
            _ => null,
        };
    }
}
