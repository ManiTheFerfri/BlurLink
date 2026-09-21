using BlurLink.Contracts;
using BlurLink.Platform;
using BlurLink.Shell.ViewModels;
using Xunit;

namespace BlurLink.Shell.Tests;

/// <summary>
/// Portable single-exe: the embed check follows the active Platform map.
/// Moved from Core.Tests with the M4 WPF cut (the Desktop assembly is
/// deleted), re-pointed at the Shell assembly and the Shell map.
/// </summary>
public sealed class ShellEmbedMapTests
{
    [Fact]
    public void HasEmbedded_FollowsActiveMap()
    {
        // The check must follow the active Platform map, not a hardcoded
        // shell literal: point the map at a resource that really exists in
        // the Shell assembly (the staged helper when Native/* is present,
        // else any always-embedded Shell resource) and it must read true.
        var shell = typeof(JoinViewModel).Assembly;
        var names = shell.GetManifestResourceNames();
        Assert.NotEmpty(names);
        var helperKey = names.FirstOrDefault(n => n.EndsWith("Native.blurlink-net.exe", StringComparison.Ordinal))
            ?? names.First();
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [helperKey] = BlurLinkConstants.HelperExeName,
        };
        try
        {
            HelperLauncher.ConfigureResources(shell, map);
            Assert.True(HelperLauncher.HasEmbeddedHelper);
        }
        finally
        {
            HelperLauncher.ConfigureResources(typeof(HelperLauncher).Assembly, HelperLauncher.ShellEmbeddedFiles);
        }
    }
}
