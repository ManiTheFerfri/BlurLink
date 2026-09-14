using BlurLink.Contracts;
using BlurLink.Core.Config;
using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// The redaction guard must accept well-formed metadata lines and reject
/// anything shaped like packet bytes (hex dumps, capture markers, blobs).
/// </summary>
public sealed class MetadataRedactorGuardTests
{
    [Fact]
    public void FormattedMetadata_PassesTheGuard()
    {
        var m = new PacketMetadata(
            DateTime.UtcNow, "100.96.21.89", 50000,
            "255.255.255.255", 12345, "100.96.47.177", 64, "forwarded");
        MetadataRedactor.AssertNoPayloadLeak(MetadataRedactor.Format(m));
    }

    [Fact]
    public void LongHexRun_IsRejected()
    {
        var dump = "12:00:00.000 100.96.21.89:50000 payload=42 4C 55 52 01 02 03 04 05 06 07 08";
        Assert.Throws<ArgumentException>(() => MetadataRedactor.AssertNoPayloadLeak(dump));
    }

    [Fact]
    public void ContinuousHexBlob_IsRejected()
    {
        var dump = "raw=424C5552010203040506070809101112131415161718";
        Assert.Throws<ArgumentException>(() => MetadataRedactor.AssertNoPayloadLeak(dump));
    }

    [Fact]
    public void CaptureMarker_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MetadataRedactor.AssertNoPayloadLeak("0x0000  45 00 00 3c  ..."));
    }

    [Fact]
    public void Base64Blob_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MetadataRedactor.AssertNoPayloadLeak("body=QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=") );
    }

    [Fact]
    public void TimestampsAndPorts_AreNotFalsePositives()
    {
        // Long digit runs and short hex-ish tokens must pass.
        MetadataRedactor.AssertNoPayloadLeak(
            "12:59:59.999 255.255.255.255:65535 -> 224.0.0.0:1 fwd 100.96.47.177:65535 len=65535 dropped-rate");
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => MetadataRedactor.AssertNoPayloadLeak(null!));
    }
}

public sealed class ConfigCorruptBackupTests
{
    [Fact]
    public void CorruptSettings_AreBackedUp_AndDefaultsLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ this is not json !!!");

        var store = new BlurLinkConfigStore();
        var cfg = store.Load(path);

        Assert.NotNull(cfg); // defaults, not a crash
        Assert.True(File.Exists(path + ".corrupt"), "backup missing");
        Assert.Contains("not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void ValidSettings_AreNotBackedUp()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{"hostOverlayIp":"100.96.47.177"}""");

        var store = new BlurLinkConfigStore();
        var cfg = store.Load(path);

        Assert.Equal("100.96.47.177", cfg.HostOverlayIp);
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void StatusResponse_CarriesWatchdogSec()
    {
        var json = """{"type":"status","active":true,"watchdogSec":15,"captured":0}""";
        var status = System.Text.Json.JsonSerializer.Deserialize<IpcStatusResponse>(json);
        Assert.NotNull(status);
        Assert.Equal(15, status!.WatchdogSec);
    }
}
