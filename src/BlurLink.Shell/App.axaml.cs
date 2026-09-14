using Avalonia;
using Avalonia.Markup.Xaml;

namespace BlurLink.Shell;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
