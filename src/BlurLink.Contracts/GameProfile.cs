using System.Text.Json.Serialization;

namespace BlurLink.Contracts;

/// <summary>
/// Named discovery profile. "Research mode" stays as the compat name, but the
/// discovery port now defaults to the verified fixed port (50001) instead of
/// null. Profiles written before the hardcode still deserialize (nullable for
/// JSON compat).
/// </summary>
public sealed class GameProfile
{
    [JsonPropertyName("profileName")]
    public string ProfileName { get; set; } = "Research mode";

    [JsonPropertyName("discoveryUdpPort")]
    public int? DiscoveryUdpPort { get; set; } = BlurLinkConstants.DiscoveryUdpPortDefault;

    [JsonPropertyName("broadcastDestination")]
    public string BroadcastDestination { get; set; } = "255.255.255.255";

    [JsonPropertyName("payloadPrefixHex")]
    public string PayloadPrefixHex { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "Fill these from an authorized local Wireshark capture.";

    public static GameProfile ResearchMode() => new();
}
