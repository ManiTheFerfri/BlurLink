using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell.Views;

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
