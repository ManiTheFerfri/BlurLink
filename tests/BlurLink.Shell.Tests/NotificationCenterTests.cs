using BlurLink.Shell.Notifications;
using Xunit;

namespace BlurLink.Shell.Tests;

public sealed class NotificationCenterTests
{
    [Fact]
    public void Notify_Adds_VisibleUntilDismissed()
    {
        var center = new NotificationCenter();

        center.Notify("Lobby may appear", "You are reaching the host.", NoticeSeverity.Progress);

        var notice = Assert.Single(center.Notices);
        Assert.Equal("Lobby may appear", notice.Title);
        Assert.Equal(NoticeSeverity.Progress, notice.Severity);

        center.Dismiss(notice);

        Assert.Empty(center.Notices);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var center = new NotificationCenter();
        center.Notify("a", "b", NoticeSeverity.Info);
        center.Notify("c", "d", NoticeSeverity.Error);

        center.Clear();

        Assert.Empty(center.Notices);
    }
}
