using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using BlurLink.Shell.ViewModels;

namespace BlurLink.Shell.Views;

public sealed partial class DialogWindow : Window
{
    public DialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Esc closes (Enter still hits the focused OK): code-behind binding
        // with the existing RelayCommand, no DataContext tricks.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(_ => Close()) });
    }

    public string MessageText => this.FindControl<TextBlock>("MessageBlock")?.Text ?? string.Empty;

    public void SetMessage(string title, string message)
    {
        Title = title;
        var block = this.FindControl<TextBlock>("MessageBlock");
        if (block is not null)
        {
            block.Text = message;
        }
    }

    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var dialog = new DialogWindow();
        dialog.SetMessage(title, message);
        await dialog.ShowDialog(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
