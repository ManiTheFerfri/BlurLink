// Central constants shared by the GUI and the native helper.
// No Blur protocol constants live here: ports/signatures are user-supplied
// research values, never hardcoded game facts.
namespace BlurLink.Contracts;

public static class BlurLinkConstants
{
    /// <summary>Helper executable name (elevated).</summary>
    public const string HelperExeName = "blurlink-net.exe";

    /// <summary>Named-pipe prefix. The full pipe name is per-launch random.</summary>
    public const string PipeNamePrefix = "BlurLink-";

    /// <summary>IPC protocol version exchanged on first message.</summary>
    public const int IpcProtocolVersion = 1;

    /// <summary>WinDivert NETWORK layer is the only layer v1 may use.</summary>
    public const string WinDivertLayer = "NETWORK";

    /// <summary>WinDivert priority used to observe outbound packets before most filters.</summary>
    public const int WinDivertPriority = 0;

    /// <summary>Default safe rate limit (forwarded packets/second).</summary>
    public const int DefaultRateLimitPerSecond = 10;

    /// <summary>Default burst size for the token bucket.</summary>
    public const int DefaultRateLimitBurst = 20;

    /// <summary>Hard cap for the user-configurable rate limit.</summary>
    public const int MaxRateLimitPerSecond = 100;

    /// <summary>Hard cap for burst.</summary>
    public const int MaxRateLimitBurst = 200;

    /// <summary>Maximum packet-metadata events retained for the GUI table.</summary>
    public const int MaxRecentPackets = 100;

    /// <summary>Maximum bounded queue depth inside the helper.</summary>
    public const int HelperQueueCapacity = 1024;

    /// <summary>Supported platform.</summary>
    public const string SupportedPlatform = "Windows 10/11 x64";

    /// <summary>
    /// Canonical log levels, ascending verbosity (index order is the
    /// comparison order used by the logger). Do not mutate.
    /// </summary>
    public static readonly string[] LogLevels = { "Error", "Warning", "Information", "Debug" };

    /// <summary>Level used when a stored/imported value is missing or unknown.</summary>
    public const string DefaultLogLevel = "Information";

    /// <summary>
    /// UDP port BlurLink's own host-introduction packets are sent to. This is
    /// BlurLink's constant, not an inferred Blur protocol value. Deliberately
    /// NOT Blur's discovery port: sending an introduction there would feed
    /// garbage to the host's Blur.
    /// </summary>
    public const int HostAnnounceUdpPort = 47811;

    /// <summary>Hard cap on accepted players in host mode.</summary>
    public const int HostMaxPlayers = 8;

    /// <summary>
    /// Seconds without an introduction before a player leaves the host's
    /// filter. The roster entry is kept so the GUI keeps its counters.
    /// </summary>
    public const int HostPlayerExpirySeconds = 45;

    /// <summary>True when <paramref name="level"/> is a canonical log level.</summary>
    public static bool IsValidLogLevel(string? level)
        => level is not null && Array.IndexOf(LogLevels, level) >= 0;

    /// <summary>Maps any value onto a canonical log level (unknown → default).</summary>
    public static string NormalizeLogLevel(string? level)
        => IsValidLogLevel(level) ? level! : DefaultLogLevel;
}
