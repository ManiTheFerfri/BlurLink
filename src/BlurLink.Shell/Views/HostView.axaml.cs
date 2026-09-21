using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell.Views;

public sealed partial class HostView : UserControl
{
    public HostView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
