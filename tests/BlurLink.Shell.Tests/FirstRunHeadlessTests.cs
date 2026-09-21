using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Shell.Tests;

public sealed class FirstRunHeadlessTests
{
    [AvaloniaFact]
    public void FreshConfig_PortStepFixedDoneOthersUndone_AndSkipDismisses()
    {
        // Task A (renamed from FreshConfig_ShowsAllStepsUndone_AndSkipDismisses):
        // the port step is fixed-display auto-done; the other three stay undone
        // on a fresh config.
        var config = BlurLinkConfig.CreateDefault();
        var settings = new BridgeSettingsViewModel(config, () => { }, () => { });
        var vm = new FirstRunViewModel(config, settings, () => new[] { @"C:\Games\Blur\Blur.exe" }, _ => { }, () => { });
        var view = new FirstRunView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Assert.Equal(4, vm.Steps.Count);
            Assert.Equal("50001", settings.DiscoveryPort); // Task A: fixed default, not empty
            Assert.False(vm.Steps[0].Done);
            Assert.False(vm.Steps[1].Done);
            Assert.True(vm.Steps[2].Done);
            Assert.Equal("Discovery port is fixed at 50001 (verified).", vm.Steps[2].Detail);
            Assert.False(vm.Steps[3].Done);
            Assert.Equal("Research mode", vm.VerifiedBadge);
            vm.SkipCommand.Execute(null);
            Assert.True(config.FirstRunDismissed);
        }
        finally
        {
            window.Close();
        }
    }
}
