using BlurLink.Contracts;
using BlurLink.Core.Config;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class ConfigMigrationTests
{
    [Fact]
    public void MissingFile_YieldsFixedDiscoveryPort()
    {
        // Task A: the research-mode null default retired by evidence
        // (2026-09-12 capture + 2026-09-15 live lobby) — fresh configs pin 50001.
        var store = new BlurLinkConfigStore();
        var cfg = store.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json"));

        Assert.Equal(BlurLinkConstants.DiscoveryUdpPortDefault, cfg.DiscoveryUdpPort);
        Assert.Equal(50001, cfg.DiscoveryUdpPort);
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
        // Task A: legacy files without a port migrate to the fixed default.
        Assert.Equal(BlurLinkConstants.DiscoveryUdpPortDefault, cfg.DiscoveryUdpPort);
    }

    [Fact]
    public void NullOrZeroPort_MigratesToFixedDefault_WhileExplicitOverrideSurvives()
    {
        // Task A: old files with null/0 land on 50001; an Advanced manual
        // override (nonzero) is never clobbered by migration.
        Assert.Equal(50001, BlurLinkConstants.DiscoveryUdpPortDefault);
        var fromNull = BlurLinkConfigStore.ImportJson("""{"hostOverlayIp":"100.96.47.177","discoveryUdpPort":null}""");
        Assert.Equal(50001, fromNull.DiscoveryUdpPort);
        var fromZero = BlurLinkConfigStore.ImportJson("""{"hostOverlayIp":"100.96.47.177","discoveryUdpPort":0}""");
        Assert.Equal(50001, fromZero.DiscoveryUdpPort);
        var explicitOverride = BlurLinkConfigStore.ImportJson("""{"discoveryUdpPort":60001}""");
        Assert.Equal(60001, explicitOverride.DiscoveryUdpPort);
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
            // Guided first run (Task 8): verified-profile badge fields plus the
            // profile → host-IP memory (R8: addresses only, never payloads).
            "verifiedProfileName", "verifiedProfileDate", "firstRunDismissed", "hostIpByProfile",
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
