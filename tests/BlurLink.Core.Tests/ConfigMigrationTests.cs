using BlurLink.Contracts;
using BlurLink.Core.Config;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class ConfigMigrationTests
{
    [Fact]
    public void MissingFile_YieldsResearchDefaults()
    {
        var store = new BlurLinkConfigStore();
        var cfg = store.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json"));

        Assert.Null(cfg.DiscoveryUdpPort); // Research mode: unknown
        Assert.Equal("255.255.255.255", cfg.BroadcastDestination);
        Assert.True(cfg.PreserveOriginalBroadcast);
        Assert.Equal(BlurLinkConstants.DefaultRateLimitPerSecond, cfg.RateLimitPerSecond);
    }

    [Fact]
    public void LegacyJson_MigratesForward()
    {
        var legacy = """{"hostOverlayIp":"100.96.47.177"}""";
        var cfg = BlurLinkConfigStore.ImportJson(legacy);

        Assert.Equal("100.96.47.177", cfg.HostOverlayIp);
        Assert.Equal("255.255.255.255", cfg.BroadcastDestination);
        Assert.True(cfg.PreserveOriginalBroadcast);
        Assert.Equal("join", cfg.LastMode);
        Assert.Equal(BlurLinkConstants.DefaultRateLimitPerSecond, cfg.RateLimitPerSecond);
    }

    [Fact]
    public void UnknownLogLevel_IsNormalizedOnImport()
    {
        // A hand-edited or foreign settings.json must never leave the logger
        // (and the Settings drop-down) on a level that does not exist.
        var cfg = BlurLinkConfigStore.ImportJson("""{"hostOverlayIp":"100.96.47.177","logLevel":"Banana"}""");

        Assert.Equal(BlurLinkConstants.DefaultLogLevel, cfg.LogLevel);
        Assert.True(BlurLinkConstants.IsValidLogLevel(cfg.LogLevel));
    }

    [Fact]
    public void RateLimit_ClampedOnSave()
    {
        var store = new BlurLinkConfigStore();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(dir, "settings.json");

        var cfg = BlurLinkConfig.CreateDefault();
        cfg.RateLimitPerSecond = 9999;
        store.Save(cfg, path);

        Assert.Equal(BlurLinkConstants.MaxRateLimitPerSecond, store.Load(path).RateLimitPerSecond);
    }

    [Fact]
    public void ExportImport_RoundTrips_WithoutPayloads()
    {
        var cfg = new BlurLinkConfig
        {
            BlurExePath = @"C:\Games\Blur\Blur.exe",
            HostOverlayIp = "100.96.47.177",
            DiscoveryUdpPort = 12345,
            PayloadPrefixHex = "42 4C",
        };
        var json = BlurLinkConfigStore.ExportJson(cfg);

        // Schema allow-list: only documented scalar fields, no payload/blob keys.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "blurExePath", "blurArgs", "lastMode", "selectedAdapterIfIndex", "hostOverlayIp",
            "discoveryUdpPort", "broadcastDestination", "payloadPrefixHex",
            "preserveOriginalBroadcast", "rateLimitPerSecond", "logLevel",
            // Host mode. hostAcceptedPlayers is a list of IPv4 addresses the
            // host accepted or revoked — metadata, never a payload or blob.
            "hostAutoAccept", "hostAcceptedPlayers",
        };
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            Assert.Contains(prop.Name, allowed);
        }

        var back = BlurLinkConfigStore.ImportJson(json);
        Assert.Equal(cfg.HostOverlayIp, back.HostOverlayIp);
        Assert.Equal(12345, back.DiscoveryUdpPort);
        Assert.Equal("42 4C", back.PayloadPrefixHex);
    }

    [Fact]
    public void HostModeDefaults_AreSafeAndPresent()
    {
        var cfg = BlurLinkConfig.CreateDefault();
        Assert.True(cfg.HostAutoAccept);
        Assert.Empty(cfg.HostAcceptedPlayers);
        Assert.Equal(47811, BlurLinkConstants.HostAnnounceUdpPort);
        Assert.Equal(8, BlurLinkConstants.HostMaxPlayers);
        Assert.Equal(45, BlurLinkConstants.HostPlayerExpirySeconds);
    }

    [Fact]
    public void HostModeFields_SurviveARoundTrip()
    {
        var cfg = BlurLinkConfig.CreateDefault();
        cfg.HostAutoAccept = false;
        cfg.HostAcceptedPlayers.Add("25.1.2.3");
        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        var back = System.Text.Json.JsonSerializer.Deserialize<BlurLinkConfig>(json)!;
        Assert.False(back.HostAutoAccept);
        Assert.Equal(new[] { "25.1.2.3" }, back.HostAcceptedPlayers);
    }
}
