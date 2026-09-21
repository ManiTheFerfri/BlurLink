using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Shell.Tests;

public sealed class HostHeadlessTests
{
    [AvaloniaFact]
    public void WaitingStory_RendersWithoutClaimingFailure()
    {
        var vm = HostViewModel.ForTests(new ScriptedChannel());
        vm.ApplyStatusForTests(new IpcStatusResponse { HostActive = true });
        var view = new HostView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Assert.Equal("host-waiting", vm.Story.Code);
            var headline = view.FindControl<TextBlock>("HostStoryHeadline");
            Assert.NotNull(headline);
            Assert.Equal("Waiting for a player", headline.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PlayerRow_RendersOverlayAndLan()
    {
        var vm = HostViewModel.ForTests(new ScriptedChannel());
        vm.ApplyStatusForTests(new IpcStatusResponse
        {
            HostActive = true,
            HostPlayers =
            {
                new HostPlayerStatus { OverlayIp = "10.0.0.200", LanIp = "192.168.0.200", BlurSourcePort = 50001, InFilter = true },
            },
        });
        var view = new HostView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var list = view.FindControl<ItemsControl>("PlayerList");
            Assert.NotNull(list);
            Assert.Single(vm.Players);
            Assert.Equal("10.0.0.200", vm.Players[0].OverlayIp);
        }
        finally
        {
            window.Close();
        }
    }
}
