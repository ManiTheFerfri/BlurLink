using System.Text.Json;
using BlurLink.Contracts;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// The helper builds its status JSON by hand (string concatenation in main.cpp),
/// not by serialising these classes. So the wire names are duplicated in two
/// languages, and drift between them would show up as silently-zero counters in
/// the Host tab rather than as an error. These tests pin the wire names from the
/// helper's side, using a literal shaped exactly like the helper's output.
/// </summary>
public sealed class HostIpcContractTests
{
    private const string HelperShapedStatus = """
    {"type":"status","active":false,"watchdogSec":15,"filter":"f","captured":1,
     "forwarded":0,"reinjected":9,"dropped":0,"injectionErrors":0,
     "fragmentsRejected":0,"dedupSkipped":0,"lastError":"","routeInterface":"",
     "sniffActive":false,"sniffResults":[],
     "hostActive":true,"hostForwardsHeard":4,"hostRepliesForwarded":3,
     "hostBroadcastReplies":1,"hostAmbiguousReplies":2,"hostUnmatchedReplies":5,
     "hostReinjected":9,"hostInjectionErrors":0,"hostAnnounceRejected":1,
     "hostFilterReopens":2,"hostCollisions":1,"hostLastNote":"cap reached",
     "hostPlayers":[{"overlayIp":"25.1.2.3","lanIp":"192.168.1.50",
       "blurSourcePort":51234,"firstSeenMs":10,"lastSeenMs":20,
       "forwardsHeard":4,"repliesForwarded":3,"inFilter":true}],
     "recent":[]}
    """;

    [Fact]
    public void HelperShapedHostStatus_ParsesIntoTheStatusResponse()
    {
        var s = JsonSerializer.Deserialize<IpcStatusResponse>(HelperShapedStatus)!;

        Assert.True(s.HostActive);
        Assert.Equal(4, s.HostForwardsHeard);
        Assert.Equal(3, s.HostRepliesForwarded);
        Assert.Equal(1, s.HostBroadcastReplies);
        Assert.Equal(2, s.HostAmbiguousReplies);
        Assert.Equal(5, s.HostUnmatchedReplies);
        Assert.Equal(9, s.HostReinjected);
        Assert.Equal(0, s.HostInjectionErrors);
        Assert.Equal(1, s.HostAnnounceRejected);
        Assert.Equal(2, s.HostFilterReopens);
        Assert.Equal(1, s.HostCollisions);
        Assert.Equal("cap reached", s.HostLastNote);

        // The bridge fields must still parse — host mode is additive.
        Assert.Equal(9, s.Reinjected);
    }

    [Fact]
    public void HelperShapedHostPlayers_ParseWithEveryField()
    {
        var s = JsonSerializer.Deserialize<IpcStatusResponse>(HelperShapedStatus)!;

        var p = Assert.Single(s.HostPlayers);
        Assert.Equal("25.1.2.3", p.OverlayIp);
        Assert.Equal("192.168.1.50", p.LanIp);
        Assert.Equal(51234, p.BlurSourcePort);
        Assert.Equal(10, p.FirstSeenMs);
        Assert.Equal(20, p.LastSeenMs);
        Assert.Equal(4, p.ForwardsHeard);
        Assert.Equal(3, p.RepliesForwarded);
        Assert.True(p.InFilter);
    }

    [Fact]
    public void StatusWithoutHostFields_DefaultsToHostModeOff()
    {
        // A helper that predates host mode, or a bridge-only session.
        var s = JsonSerializer.Deserialize<IpcStatusResponse>(
            """{"type":"status","active":true}""")!;

        Assert.False(s.HostActive);
        Assert.Equal(0, s.HostForwardsHeard);
        Assert.Empty(s.HostPlayers);
    }

    [Fact]
    public void StartHostRequest_UsesTheDocumentedTypeString()
    {
        var req = new IpcStartHostRequest
        {
            Token = "t",
            DiscoveryUdpPort = 50001,
            AdapterIfIndex = 7,
        };
        var json = JsonSerializer.Serialize(req);

        Assert.Contains("\"type\":\"start_host\"", json);
        Assert.Contains("\"discoveryUdpPort\":50001", json);
        Assert.Contains("\"adapterIfIndex\":7", json);

        // These are exactly the keys ConfigFromHostStart reads in main.cpp.
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("discoveryUdpPort", out _));
        Assert.True(doc.RootElement.TryGetProperty("adapterIfIndex", out _));
    }

    [Fact]
    public void RevokeHostPlayerRequest_UsesTheDocumentedTypeString()
    {
        var req = new IpcRevokeHostPlayerRequest { Token = "t", OverlayIp = "25.1.2.3" };
        var json = JsonSerializer.Serialize(req);

        Assert.Contains("\"type\":\"revoke_host_player\"", json);
        Assert.Contains("\"overlayIp\":\"25.1.2.3\"", json);
    }

    [Fact]
    public void CommandTypeConstants_MatchTheHelperDispatch()
    {
        Assert.Equal("start_host", IpcMessageTypes.StartHost);
        Assert.Equal("revoke_host_player", IpcMessageTypes.RevokeHostPlayer);
    }
}
