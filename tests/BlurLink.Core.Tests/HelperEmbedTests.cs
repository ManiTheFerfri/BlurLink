using BlurLink.Contracts;
using BlurLink.Desktop.ViewModels;
using BlurLink.Platform;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>Portable single-exe: embedded-helper extraction rules.</summary>
public sealed class HelperEmbedTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ExtractStagedFile_WritesValidExe_Atomically()
    {
        var dir = TempDir();
        var dest = Path.Combine(dir, "blurlink-net.exe");
        var payload = new byte[] { (byte)'M', (byte)'Z', 0x01, 0x02, 0x03 };
        using var ms = new MemoryStream(payload);

        var (path, written) = HelperLauncher.ExtractStagedFile(ms, dest);

        Assert.Equal(dest, path);
        Assert.True(written);
        Assert.Equal(payload, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".tmp"), "no temp residue may remain");
    }

    [Fact]
    public void ExtractStagedFile_RejectsNonExecutable()
    {
        var dir = TempDir();
        using var ms = new MemoryStream(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
        Assert.Throws<InvalidDataException>(() =>
            HelperLauncher.ExtractStagedFile(ms, Path.Combine(dir, "x.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "x.exe")));
    }

    [Fact]
    public void ExtractStagedFile_SkipsWrite_WhenIdentical()
    {
        var dir = TempDir();
        var dest = Path.Combine(dir, "blurlink-net.exe");
        var payload = new byte[] { (byte)'M', (byte)'Z', 0xAA };
        File.WriteAllBytes(dest, payload);
        var before = File.GetLastWriteTimeUtc(dest);

        using var ms = new MemoryStream(payload);
        var (_, written) = HelperLauncher.ExtractStagedFile(ms, dest);

        Assert.False(written);
        Assert.Equal(before, File.GetLastWriteTimeUtc(dest));
    }

    [Fact]
    public void HasEmbedded_FollowsActiveMap()
    {
        // The check must follow the active Platform map, not a hardcoded
        // shell literal: point the map at a resource that really exists in
        // the Desktop assembly (the staged helper when Native/* is present,
        // else the always-embedded WPF g.resources) and it must read true.
        var desktop = typeof(HostViewModel).Assembly;
        var names = desktop.GetManifestResourceNames();
        var helperKey = names.FirstOrDefault(n => n.EndsWith("Native.blurlink-net.exe", StringComparison.Ordinal))
            ?? names.First(n => n.EndsWith(".g.resources", StringComparison.Ordinal));
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [helperKey] = BlurLinkConstants.HelperExeName,
        };
        try
        {
            HelperLauncher.ConfigureResources(desktop, map);
            Assert.True(HelperLauncher.HasEmbeddedHelper);
        }
        finally
        {
            HelperLauncher.ConfigureResources(typeof(HelperLauncher).Assembly, HelperLauncher.DesktopEmbeddedFiles);
        }
    }

    [Fact]
    public void ResolveHelperPath_FallsBackToSideBySide()
    {
        // Dev builds stage no embedded helper; resolution must point at the
        // side-by-side dev layout (Launch raises the clear error if absent).
        if (HelperLauncher.HasEmbeddedHelper)
        {
            return; // portable build agent: covered by launch tests instead
        }

        var launcher = new HelperLauncher();
        try
        {
            Assert.EndsWith("blurlink-net.exe", launcher.HelperPath(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            launcher.Dispose();
        }
    }
}
