using BlurLink.Platform;
using BlurLink.Desktop.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Regression: constructing the main view-model must never fault, even
/// though adapter selection fires change notifications mid-construction
/// (previously: reentrant OnConfigChanged hit unassigned VMs → adapter
/// enumeration reported failure on a healthy machine).
/// </summary>
public sealed class MainViewModelConstructionTests
{
    [Fact]
    public void ConstructsCleanly_WithAdaptersLoaded()
    {
        var vm = new MainViewModel();
        try
        {
            Assert.NotNull(vm.Join.SelectedAdapter);
            Assert.Equal(string.Empty, vm.Join.Message);
            Assert.Equal(7, vm.Join.PreflightItems.Count);
            Assert.Contains(vm.Join.PreflightItems, i => i.Label == "Overlay adapter" && i.Ok);
        }
        finally
        {
            vm.Join.Dispose();
            vm.Dispose();
        }
    }

    [Fact]
    public void Navigate_RefreshesAdapters_WithoutThrowing()
    {
        var vm = new MainViewModel();
        try
        {
            vm.CurrentView = "Settings";
            vm.RefreshAllAdapters();
            Assert.NotEmpty(vm.Join.Adapters);
        }
        finally
        {
            vm.Join.Dispose();
            vm.Dispose();
        }
    }

    [Fact]
    public void AdapterWatcher_StartStop_DoesNotThrow()
    {
        using var watcher = new AdapterWatcher();
        var fired = false;
        watcher.Changed += () => fired = true;
        Assert.False(fired); // subscribing alone fires nothing
    }

    [Fact]
    public void AttachToMissingGame_ReturnsFalse()
    {
        using var watcher = new Platform.BlurProcessWatcher();
        Assert.False(watcher.AttachToRunning("DefinitelyNotARealProcessName12345"));
        Assert.False(watcher.Watching);
    }

    [Fact]
    public void AttachToOwnTestHost_WatchesAndReleases()
    {
        using var watcher = new Platform.BlurProcessWatcher();
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        Assert.True(watcher.AttachToRunning(self));
        Assert.True(watcher.Watching);
        Assert.True(watcher.PrimaryPid > 0);
        Assert.True(watcher.WatchedCount >= 1);
        watcher.Stop();
        Assert.False(watcher.Watching);
        Assert.Equal(0, watcher.PrimaryPid);
    }

    [Fact]
    public void AttachPrefersNothing_WithoutPathStillFindsByName()
    {
        using var watcher = new Platform.BlurProcessWatcher();
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        Assert.True(watcher.AttachToRunning(self, expectedPath: "C:\\no\\such\\game.exe"));
    }

    [Fact]
    public void BlurArgs_MigratesToEmpty_AndRoundTrips()
    {
        var cfg = Core.Config.BlurLinkConfigStore.ImportJson("""{"hostOverlayIp":"100.96.47.177"}""");
        Assert.Equal(string.Empty, cfg.BlurArgs);
        cfg.BlurArgs = "-windowed";
        var back = Core.Config.BlurLinkConfigStore.ImportJson(
            Core.Config.BlurLinkConfigStore.ExportJson(cfg));
        Assert.Equal("-windowed", back.BlurArgs);
    }
}
