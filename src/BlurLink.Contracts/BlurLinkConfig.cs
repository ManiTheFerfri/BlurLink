using System.Text.Json.Serialization;

namespace BlurLink.Contracts;

/// <summary>
/// Local per-user configuration. Persisted to
/// %LocalAppData%\BlurLink\settings.json. Never contains packet payloads.
/// </summary>
public sealed class BlurLinkConfig
{
    [JsonPropertyName("blurExePath")]
    public string BlurExePath { get; set; } = string.Empty;

    /// <summary>Optional extra command-line arguments for Blur.exe.</summary>
    [JsonPropertyName("blurArgs")]
    public string BlurArgs { get; set; } = string.Empty;

    [JsonPropertyName("lastMode")]
    public string LastMode { get; set; } = "join";

    [JsonPropertyName("selectedAdapterIfIndex")]
    public int SelectedAdapterIfIndex { get; set; }

    [JsonPropertyName("hostOverlayIp")]
    public string HostOverlayIp { get; set; } = string.Empty;

    /// <summary>Null = Research mode (unknown yet).</summary>
    [JsonPropertyName("discoveryUdpPort")]
    public int? DiscoveryUdpPort { get; set; }

    [JsonPropertyName("broadcastDestination")]
    public string BroadcastDestination { get; set; } = "255.255.255.255";

    /// <summary>Hex bytes like "42 4C 55 52". Empty = no signature constraint.</summary>
    [JsonPropertyName("payloadPrefixHex")]
    public string PayloadPrefixHex { get; set; } = string.Empty;

    [JsonPropertyName("preserveOriginalBroadcast")]
    public bool PreserveOriginalBroadcast { get; set; } = true;

    [JsonPropertyName("rateLimitPerSecond")]
    public int RateLimitPerSecond { get; set; } = BlurLinkConstants.DefaultRateLimitPerSecond;

    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Host mode: accept introduced players without a click. Introductions are
    /// unauthenticated, so this is a documented trade-off — see SECURITY.md.
    /// </summary>
    [JsonPropertyName("hostAutoAccept")]
    public bool HostAutoAccept { get; set; } = true;

    /// <summary>
    /// Host mode: player overlay addresses the host has accepted or revoked.
    /// Addresses only — never payloads, never game data.
    /// </summary>
    [JsonPropertyName("hostAcceptedPlayers")]
    public List<string> HostAcceptedPlayers { get; set; } = new();

    public static BlurLinkConfig CreateDefault() => new();
}
