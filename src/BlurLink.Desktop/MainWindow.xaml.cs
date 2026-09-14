using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BlurLink.Desktop.ViewModels;

namespace BlurLink.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _stopBeforeClose;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentView))
            {
                Show(_vm.CurrentView);
            }
        };

        // Wire per-view DataContexts.
        JoinPane.DataContext = _vm.Join;
        HostPane.DataContext = _vm.Host;
        SettingsPane.DataContext = _vm.Settings;

        Show(_vm.CurrentView);

        SourceInitialized += (_, _) => UseDarkTitleBar();

        Closing += async (_, e) =>
        {
            if (_vm.Join.BridgeRunning && !_stopBeforeClose)
            {
                // Deterministic shutdown: cancel this close, stop the bridge
                // to completion (helper exit verified, force-kill fallback),
                // then close again. Closing mid-await could otherwise race
                // process exit and leave cleanup to the helper's watchdog.
                e.Cancel = true;
                _stopBeforeClose = true;
                try
                {
                    await _vm.Join.StopAsync();
                }
                catch
                {
                    // best effort — StopAsync already force-kills on failure
                }

                Close();
                return;
            }

            _vm.Join.Dispose();
            _vm.Dispose();
        };
    }

    private void Show(string view)
    {
        JoinPane.Visibility = view == "Join" ? Visibility.Visible : Visibility.Collapsed;
        HostPane.Visibility = view == "Host" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPane.Visibility = view == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AnimateIn(view switch
        {
            "Host" => HostPane,
            "Settings" => SettingsPane,
            _ => (FrameworkElement)JoinPane,
        });
    }

    /// <summary>Fade + slide micro-transition for the incoming pane.</summary>
    private static void AnimateIn(UIElement pane)
    {
        pane.Opacity = 0;
        var slide = new TranslateTransform(16, 0);
        pane.RenderTransform = slide;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        pane.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
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

            var on = 1;
            _ = DwmSetWindowAttribute(
                new System.Windows.Interop.WindowInteropHelper(this).Handle,
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
