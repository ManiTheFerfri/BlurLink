using BlurLink.Contracts;
using BlurLink.Shell.Notifications;
using BlurLink.Shell.ViewModels;
using Xunit;

namespace BlurLink.Shell.Tests;

public sealed class SessionNotifierTests
{
    [Theory]
    [InlineData("bridge-idle", "bridge-forwarding", true)]
    [InlineData("bridge-forwarding", "bridge-forwarding", false)]
    [InlineData("host-waiting", "host-forwarding", true)]
    [InlineData("idle", "blocked", true)]
    [InlineData("idle", "idle", false)]
    public void Watch_EmitsOnce_PerTransition(string prev, string next, bool expected)
    {
        var notice = SessionNotifier.Watch(prev, next);

        Assert.Equal(expected, notice is not null);
    }

    [Fact]
    public void ForwardingNotice_PointsAtHostMode()
    {
        var notice = SessionNotifier.Watch("bridge-idle", "bridge-forwarding");

        Assert.NotNull(notice);
        Assert.Contains("Host mode", notice.Message);
    }

    [Fact]
    public void LostConnection_IsAnError()
    {
        var notice = SessionNotifier.Watch("bridge-forwarding", "failed");

        Assert.NotNull(notice);
        Assert.Equal(NoticeSeverity.Error, notice.Severity);
    }

    [Fact]
    public void RememberHost_StoresUnderTheActiveProfileName()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.VerifiedProfileName = "Blur LAN (verified 2026-09-14)";
        config.HostOverlayIp = "10.0.0.10";

        MainViewModel.RememberHost(config);

        Assert.Equal("10.0.0.10", config.HostIpByProfile["Blur LAN (verified 2026-09-14)"]);
    }

    [Fact]
    public void RecallHost_FallsBackToResearchMode_WhenNoVerifiedProfile()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.HostOverlayIp = "10.0.0.11";

        Assert.Equal("Research mode", MainViewModel.ActiveProfileName(config));
        MainViewModel.RememberHost(config);

        Assert.Equal("10.0.0.11", MainViewModel.RecallHost(config));
        Assert.Null(MainViewModel.RecallHost(BlurLinkConfig.CreateDefault()));
    }
}
