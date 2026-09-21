using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using BlurLink.Shell.ViewModels;

namespace BlurLink.Shell.Views;

public sealed partial class MainWindow : Window
{
    private bool _stopBeforeClose;

    private MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnVmChanged;
            }
        };

        Opened += (_, _) =>
        {
            UpdateStatusDot();
            UseDarkTitleBar();
        };

        Closing += async (_, e) =>
        {
            if (DataContext is not MainViewModel vm)
            {
                return;
            }

            if (vm.Join.Session.BridgeRunning && !_stopBeforeClose)
            {
                // Deterministic shutdown: cancel this close, stop the bridge
                // to completion (helper exit verified, force-kill fallback),
                // then close again.
                e.Cancel = true;
                _stopBeforeClose = true;
                try
                {
                    await vm.Join.Session.StopAsync();
                }
                catch
                {
                    // best effort — StopAsync already force-kills on failure
                }

                Close();
                return;
            }

            vm.Join.Dispose();
            vm.Dispose();
        };
    }

    /// <summary>
    /// Task 13: navigation seam for the headless tests (and keyboard users).
    /// Delegates to the VM, then lands keyboard focus on the first focusable
    /// control inside the new pane — best effort, a no-op when the pane has
    /// not materialized yet or nothing in it can take focus.
    /// </summary>
    public void Navigate(string view)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.NavigateCommand.Execute(view);
        var pane = this.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault();
        var target = pane?.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c is Button or TextBox or ComboBox && c.Focusable && c.IsEffectivelyVisible);
        target?.Focus();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusChipKind))
        {
            UpdateStatusDot();
        }
    }

    private void UpdateStatusDot()
    {
        var dot = this.FindControl<Ellipse>("StatusDot");
        if (dot is null)
        {
            return;
        }

        // Try-lookup: resource hosts that are not attached yet (or a host app
        // without the token dictionary, e.g. headless tests) report misses as
        // UnsetValue instead of throwing — keep the XAML default then.
        if (Avalonia.Controls.ResourceNodeExtensions.TryFindResource(this, Vm.StatusChipKind switch
            {
                StatusChipKind.Running => "Success",
                StatusChipKind.Lost => "Danger",
                _ => "Muted",
            }, out var brush) && brush is IBrush fill)
        {
            dot.Fill = fill;
        }
    }

    /// <summary>
    /// Dark-mode title bar on Windows 10 1809+ via DWM. Best effort:
    /// older/odd Windows versions simply keep the classic light chrome.
    /// </summary>
    private void UseDarkTitleBar()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                return;
            }

            var handle = TryGetPlatformHandle()?.Handle;
            if (handle is null)
            {
                return;
            }

            var on = 1;
            _ = DwmSetWindowAttribute(
                handle.Value,
                DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref on, sizeof(int));
        }
        catch
        {
            // cosmetic only — never block startup
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
