using System.Text.Json.Serialization;

namespace BlurLink.Contracts;

// ---------------------------------------------------------------------------
// IPC envelope. Every message on the named pipe is a single JSON object with
// a "type" discriminator, terminated by '\n'. No network listener is used.
// ---------------------------------------------------------------------------

/// <summary>Message types exchanged between GUI and helper.</summary>
public static class IpcMessageTypes
{
    public const string Hello = "hello";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Sniff = "sniff";
    public const string StopSniff = "stop_sniff";

    /// <summary>Host mode: forward the host's Blur replies to each player.</summary>
    public const string StartHost = "start_host";

    /// <summary>Host mode: drop a player and rebuild the filter.</summary>
    public const string RevokeHostPlayer = "revoke_host_player";
    public const string GetStatus = "get_status";
    public const string Shutdown = "shutdown";
    public const string Status = "status";
    public const string Error = "error";
    public const string EventPacket = "event_packet";
}

public sealed class IpcHelloMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.Hello;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = BlurLinkConstants.IpcProtocolVersion;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public sealed class IpcStartRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.Start;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("hostOverlayIp")]
    public string HostOverlayIp { get; set; } = string.Empty;

    [JsonPropertyName("discoveryUdpPort")]
    public int DiscoveryUdpPort { get; set; }

    [JsonPropertyName("broadcastDestination")]
    public string BroadcastDestination { get; set; } = "255.255.255.255";

    [JsonPropertyName("payloadPrefixHex")]
    public string PayloadPrefixHex { get; set; } = string.Empty;

    [JsonPropertyName("preserveOriginalBroadcast")]
    public bool PreserveOriginalBroadcast { get; set; } = true;

    /// <summary>
    /// Our own overlay address. When set (and <see cref="AnnounceToHost"/> is on),
    /// the bridge introduces itself to a host running host mode so the host can
    /// map its replies back to us. Absent means the bridge behaves exactly as it
    /// did before host mode existed.
    /// </summary>
    [JsonPropertyName("localOverlayIp")]
    public string LocalOverlayIp { get; set; } = string.Empty;

    [JsonPropertyName("announceToHost")]
    public bool AnnounceToHost { get; set; } = true;

    [JsonPropertyName("rateLimitPerSecond")]
    public int RateLimitPerSecond { get; set; } = BlurLinkConstants.DefaultRateLimitPerSecond;

    [JsonPropertyName("rateLimitBurst")]
    public int RateLimitBurst { get; set; } = BlurLinkConstants.DefaultRateLimitBurst;

    [JsonPropertyName("adapterIfIndex")]
    public int AdapterIfIndex { get; set; }

    /// <summary>Observe-only reply-shape expectation from the verified profile. Null/empty = off.</summary>
    [JsonPropertyName("expectedReplyLength")]
    public int? ExpectedReplyLength { get; set; }

    /// <summary>Observe-only reply-shape expectation from the verified profile. Null/empty = off.</summary>
    [JsonPropertyName("expectedReplyPrefixHex")]
    public string ExpectedReplyPrefixHex { get; set; } = string.Empty;
}

/// <summary>
/// Host mode start. The helper validates every field again and never trusts the
/// GUI, exactly as it does for a bridge start.
/// </summary>
public sealed class IpcStartHostRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.StartHost;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("discoveryUdpPort")]
    public int DiscoveryUdpPort { get; set; }

    [JsonPropertyName("adapterIfIndex")]
    public int AdapterIfIndex { get; set; }

    /// <summary>Observe-only reply-shape expectation from the verified profile. Null/empty = off.</summary>
    [JsonPropertyName("expectedReplyLength")]
    public int? ExpectedReplyLength { get; set; }

    /// <summary>Observe-only reply-shape expectation from the verified profile. Null/empty = off.</summary>
    [JsonPropertyName("expectedReplyPrefixHex")]
    public string ExpectedReplyPrefixHex { get; set; } = string.Empty;

    [JsonPropertyName("rateLimitPerSecond")]
    public int RateLimitPerSecond { get; set; } = BlurLinkConstants.DefaultRateLimitPerSecond;

    [JsonPropertyName("rateLimitBurst")]
    public int RateLimitBurst { get; set; } = BlurLinkConstants.DefaultRateLimitBurst;
}

public sealed class IpcRevokeHostPlayerRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.RevokeHostPlayer;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("overlayIp")]
    public string OverlayIp { get; set; } = string.Empty;
}

/// <summary>
/// One player the host has accepted. Addresses and counters only — never
/// payloads, never game data.
/// </summary>
public sealed class HostPlayerStatus
{
    /// <summary>The player's overlay address, as they introduced themselves.</summary>
    [JsonPropertyName("overlayIp")]
    public string OverlayIp { get; set; } = string.Empty;

    /// <summary>The address the host's Blur replies to for this player.</summary>
    [JsonPropertyName("lanIp")]
    public string LanIp { get; set; } = string.Empty;

    /// <summary>The player's Blur source port; replies are matched on this too.</summary>
    [JsonPropertyName("blurSourcePort")]
    public int BlurSourcePort { get; set; }

    [JsonPropertyName("firstSeenMs")]
    public long FirstSeenMs { get; set; }

    [JsonPropertyName("lastSeenMs")]
    public long LastSeenMs { get; set; }

    /// <summary>
    /// Discovery packets heard from this player. **This is the prerequisite
    /// diagnostic**: zero across every player means the host is not receiving
    /// forwards at all, and host mode cannot help until it is.
    /// </summary>
    [JsonPropertyName("forwardsHeard")]
    public long ForwardsHeard { get; set; }

    [JsonPropertyName("repliesForwarded")]
    public long RepliesForwarded { get; set; }

    /// <summary>
    /// False once the player stops announcing. The entry is deliberately kept so
    /// the counters above stay visible instead of vanishing with the player.
    /// </summary>
    [JsonPropertyName("inFilter")]
    public bool InFilter { get; set; }
}

public sealed class IpcSimpleCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public sealed class IpcSniffRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.Sniff;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("broadcasts")]
    public List<string> Broadcasts { get; set; } = new();

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "out";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("durationSec")]
    public int DurationSec { get; set; } = 15;

    [JsonPropertyName("maxPackets")]
    public int MaxPackets { get; set; } = 200;
}

public sealed class SniffPortCount
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>Reply sender (inbound mode). Empty for outbound observations.</summary>
    [JsonPropertyName("srcIp")]
    public string SrcIp { get; set; } = string.Empty;
}

public sealed class IpcPacketEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.EventPacket;

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    [JsonPropertyName("srcIp")]
    public string SrcIp { get; set; } = string.Empty;

    [JsonPropertyName("srcPort")]
    public int SrcPort { get; set; }

    [JsonPropertyName("origDstIp")]
    public string OrigDstIp { get; set; } = string.Empty;

    [JsonPropertyName("origDstPort")]
    public int OrigDstPort { get; set; }

    [JsonPropertyName("forwardedDstIp")]
    public string ForwardedDstIp { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;
}

public sealed class IpcStatusResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.Status;

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    /// <summary>Helper self-exit watchdog (seconds). 0 when unreported.</summary>
    [JsonPropertyName("watchdogSec")]
    public int WatchdogSec { get; set; }

    [JsonPropertyName("filter")]
    public string Filter { get; set; } = string.Empty;

    [JsonPropertyName("captured")]
    public long Captured { get; set; }

    [JsonPropertyName("forwarded")]
    public long Forwarded { get; set; }

    [JsonPropertyName("reinjected")]
    public long Reinjected { get; set; }

    [JsonPropertyName("dropped")]
    public long Dropped { get; set; }

    [JsonPropertyName("injectionErrors")]
    public long InjectionErrors { get; set; }

    [JsonPropertyName("fragmentsRejected")]
    public long FragmentsRejected { get; set; }

    /// <summary>Echoes of our own reinjections suppressed by the dedup cache.</summary>
    [JsonPropertyName("dedupSkipped")]
    public long DedupSkipped { get; set; }

    /// <summary>Outbound broadcasts refused by the code-side payload-prefix gate
    /// (reinjected, never cloned). Observe-only refusal signal.</summary>
    [JsonPropertyName("payloadGateSkipped")]
    public long PayloadGateSkipped { get; set; }

    /// <summary>Replies evaluated against the reply-shape expectation (observe-only).</summary>
    [JsonPropertyName("replyShapeChecked")]
    public long ReplyShapeChecked { get; set; }

    /// <summary>Evaluated replies whose length or leading prefix differed (observe-only).</summary>
    [JsonPropertyName("replyShapeMismatch")]
    public long ReplyShapeMismatch { get; set; }

    /// <summary>Introductions this helper has sent to a host running host mode.</summary>
    [JsonPropertyName("announcementsSent")]
    public long AnnouncementsSent { get; set; }

    [JsonPropertyName("lastError")]
    public string LastError { get; set; } = string.Empty;

    [JsonPropertyName("routeInterface")]
    public string RouteInterface { get; set; } = string.Empty;

    [JsonPropertyName("hostActive")]
    public bool HostActive { get; set; }

    /// <summary>
    /// Host mode's own filter. Distinct from <see cref="Filter"/> (the bridge's):
    /// the two sessions are mutually exclusive, so the bridge's is empty exactly
    /// when host mode is the one running, and the Host tab would show nothing.
    /// </summary>
    [JsonPropertyName("hostFilter")]
    public string HostFilter { get; set; } = string.Empty;

    /// <summary>Discovery packets heard from players. 0 = the host sees no forwards.</summary>
    [JsonPropertyName("hostForwardsHeard")]
    public long HostForwardsHeard { get; set; }

    [JsonPropertyName("hostRepliesForwarded")]
    public long HostRepliesForwarded { get; set; }

    /// <summary>Replies addressed to a broadcast: refused, not forwarded (unverified shape).</summary>
    [JsonPropertyName("hostBroadcastReplies")]
    public long HostBroadcastReplies { get; set; }

    /// <summary>Replies two live players both claim: refused rather than sent to the wrong one.</summary>
    [JsonPropertyName("hostAmbiguousReplies")]
    public long HostAmbiguousReplies { get; set; }

    /// <summary>Replies whose address matched a player but whose port did not.</summary>
    [JsonPropertyName("hostUnmatchedReplies")]
    public long HostUnmatchedReplies { get; set; }

    [JsonPropertyName("hostReinjected")]
    public long HostReinjected { get; set; }

    [JsonPropertyName("hostInjectionErrors")]
    public long HostInjectionErrors { get; set; }

    [JsonPropertyName("hostAnnounceRejected")]
    public long HostAnnounceRejected { get; set; }

    [JsonPropertyName("hostFilterReopens")]
    public long HostFilterReopens { get; set; }

    [JsonPropertyName("hostCollisions")]
    public long HostCollisions { get; set; }

    [JsonPropertyName("hostLastNote")]
    public string HostLastNote { get; set; } = string.Empty;

    [JsonPropertyName("hostPlayers")]
    public List<HostPlayerStatus> HostPlayers { get; set; } = new();

    [JsonPropertyName("sniffActive")]
    public bool SniffActive { get; set; }

    [JsonPropertyName("sniffResults")]
    public List<SniffPortCount> SniffResults { get; set; } = new();

    [JsonPropertyName("recent")]
    public List<IpcPacketEvent> Recent { get; set; } = new();
}

public sealed class IpcErrorResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = IpcMessageTypes.Error;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
