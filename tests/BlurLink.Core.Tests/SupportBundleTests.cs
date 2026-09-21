using BlurLink.Core.Support;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class SupportBundleTests
{
    private static string FixtureDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "blurlink.log"), "line one\nline two\n");
        File.WriteAllText(Path.Combine(dir, "helper.log"), "helper started\n");
        File.WriteAllText(Path.Combine(dir, "evil.cap"), "must never be bundled");
        File.WriteAllText(Path.Combine(dir, "payload.bin"), "must never be bundled");
        return dir;
    }

    [Fact]
    public void Create_BundlesLogsDiagnosticsAndConfig_ButNeverCaptures()
    {
        var dir = FixtureDir();
        var dest = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var zip = SupportBundle.Create(dir, "diagnostics text", """{"hostOverlayIp":"10.0.0.10"}""", dest);

        Assert.True(File.Exists(zip));
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.Name).ToList();
        Assert.Contains("blurlink.log", names);
        Assert.Contains("helper.log", names);
        Assert.Contains("diagnostics.txt", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("versions.txt", names);
        Assert.DoesNotContain("evil.cap", names);
        Assert.DoesNotContain("payload.bin", names);
    }

    [Fact]
    public void Create_Succeeds_WithAnEmptyLogDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "logs");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var zip = SupportBundle.Create(dir, "diagnostics text", "{}", dest);

        Assert.True(File.Exists(zip));
    }

    [Fact]
    public void CrashLog_Writes_TypeMessageAndStack()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var ex = new InvalidOperationException("boom");

        var path = CrashLog.Write(dir, ex);
        var text = File.ReadAllText(path);

        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom", text);
    }
}
