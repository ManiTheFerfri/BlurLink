using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
