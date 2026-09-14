using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
