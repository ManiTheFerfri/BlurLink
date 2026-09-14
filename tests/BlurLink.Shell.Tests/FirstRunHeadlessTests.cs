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
    public void FreshConfig_ShowsAllStepsUndone_AndSkipDismisses()
    {
        var config = BlurLinkConfig.CreateDefault();
        var settings = new BridgeSettingsViewModel(config, () => { }, () => { });
        var vm = new FirstRunViewModel(config, settings, () => new[] { @"C:\Games\Blur\Blur.exe" }, _ => { }, () => { });
        var view = new FirstRunView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Assert.All(vm.Steps, s => Assert.False(s.Done));
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
