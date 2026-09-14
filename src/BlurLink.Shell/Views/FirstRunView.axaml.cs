using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell.Views;

public sealed partial class FirstRunView : UserControl
{
    public FirstRunView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
