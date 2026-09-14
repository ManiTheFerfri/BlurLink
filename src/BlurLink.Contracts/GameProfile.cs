using System.Text.Json.Serialization;

namespace BlurLink.Contracts;

/// <summary>
/// Named discovery profile. v1 ships only "Research mode" with null port.
/// Verified values are entered by the user after an authorized capture.
/// </summary>
public sealed class GameProfile
{
    [JsonPropertyName("profileName")]
    public string ProfileName { get; set; } = "Research mode";

    [JsonPropertyName("discoveryUdpPort")]
    public int? DiscoveryUdpPort { get; set; }

    [JsonPropertyName("broadcastDestination")]
    public string BroadcastDestination { get; set; } = "255.255.255.255";

    [JsonPropertyName("payloadPrefixHex")]
    public string PayloadPrefixHex { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "Fill these from an authorized local Wireshark capture.";

    public static GameProfile ResearchMode() => new();
}
