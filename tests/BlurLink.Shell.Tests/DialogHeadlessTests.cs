using Avalonia.Headless.XUnit;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class DialogHeadlessTests
{
    [AvaloniaFact]
    public void Dialog_ShowsTitleAndMessage_ThenCloses()
    {
        var window = new DialogWindow();
        window.Show();
        window.SetMessage("Already running", "Only one window can hold the packet bridge.");

        Assert.Equal("Already running", window.Title);
        Assert.Contains("packet bridge", window.MessageText);

        window.Close();
    }
}
