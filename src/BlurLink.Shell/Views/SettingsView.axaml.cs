using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
