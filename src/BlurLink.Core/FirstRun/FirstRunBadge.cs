using BlurLink.Contracts;

namespace BlurLink.Core.FirstRun;

public static class FirstRunBadge
{
    public static string Text(BlurLinkConfig config)
        => string.IsNullOrWhiteSpace(config.VerifiedProfileDate)
            ? "Research mode"
            : $"Verified {config.VerifiedProfileDate.Trim()}";
}
