using System.Windows.Controls;

namespace BlurLink.Desktop.Views;

/// <summary>
/// Host mode tab. Its DataContext is set by <see cref="MainWindow"/> to the
/// main view-model's Host property, like the other panes.
/// </summary>
public partial class HostView : UserControl
{
    public HostView()
    {
        InitializeComponent();
    }
}
