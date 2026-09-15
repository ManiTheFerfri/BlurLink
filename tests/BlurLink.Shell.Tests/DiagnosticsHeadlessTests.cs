using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Core.Session;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Shell.Tests;

public sealed class DiagnosticsHeadlessTests
{
    [AvaloniaFact]
    public void EmptyLogDir_ExplainsItself()
    {
        var vm = new DiagnosticsViewModel(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var view = new DiagnosticsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Assert.Contains("No helper log yet", vm.HelperLogStatus);
            Assert.NotNull(view.FindControl<Button>("CopyLogButton"));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void DisagreeingSamples_RenderTheRefusalReason()
    {
        var config = BlurLinkConfig.CreateDefault();
        var verify = new VerifyProfileViewModel(config, _ => { }, _ => { }, _ => { }, () => { });
        verify.Sample1 = "0F 00 00 00 00 00 00 2C 01 00 00 00 AA";
        verify.Sample2 = "1F 00 00 00 00 00 00 2C 01 00 00 00 BB";
        verify.Sample3 = "0F 00 00 00 00 00 00 2C 01 00 00 00 CC";
        verify.CheckCommand.Execute(null);

        var vm = new DiagnosticsViewModel(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), verify: verify);
        var view = new DiagnosticsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var result = view.FindControl<TextBlock>("VerifyResult");
            Assert.NotNull(result);
            Assert.Contains("differs", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ReplyShapeSection_ShowsObserveOnlyCounts()
    {
        // All-zero states hide the section; non-zero states show the counts.
        var idle = new DiagnosticsViewModel(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.False(idle.HasReplyShape);
        Assert.Empty(idle.Refusals);
        Assert.Equal("No refusals recorded.", idle.RefusalsStatus);
        idle.Dispose();

        var join = SessionState.Initial with
        {
            Mode = SessionMode.Bridge,
            Phase = SessionPhase.Running,
            Counters = SessionCounters.Empty with { ReplyShapeChecked = 5, ReplyShapeMismatch = 1 },
        };
        var vm = new DiagnosticsViewModel(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            sessionStates: () => (join, SessionState.Initial));
        var view = new DiagnosticsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Assert.True(vm.HasReplyShape);
            var section = view.FindControl<Border>("ReplyShapeSection");
            Assert.NotNull(section);
            Assert.True(section.IsVisible);
            var text = view.FindControl<TextBlock>("ReplyShapeText");
            Assert.NotNull(text);
            Assert.Contains("5 checked", text.Text);
            Assert.Contains("1 mismatched", text.Text);
            Assert.Contains("never blocked", text.Text);
            Assert.NotNull(view.FindControl<ItemsControl>("RefusalsList"));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
