using System.Windows;
using BlurLink.Desktop.Services;

namespace BlurLink.Desktop;

public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
        {
            MessageBox.Show(
                "BlurLink supports Windows 10/11 x64 only. Please run the x64 build on 64-bit Windows.",
                "BlurLink — unsupported architecture",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // One GUI only. A second window would launch a second elevated helper,
        // and two helpers both diverting the same broadcasts would duplicate
        // forwarding (the helper also refuses a concurrent bridge, but never
        // getting that far is the better user experience).
        _singleInstance = SingleInstanceGuard.Acquire();
        if (!_singleInstance.IsAcquired)
        {
            if (!FocusExistingInstance())
            {
                MessageBox.Show(
                    "BlurLink is already running.\n\nOnly one window can hold the packet bridge, " +
                    "so this copy will close. Use the window that is already open.",
                    "BlurLink — already running",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        // MainWindow is created here (no StartupUri) so an unsupported
        // architecture can exit before any window ever appears.
        var window = new MainWindow();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Best effort: raise the instance that already owns the slot instead of
    /// leaving the user staring at nothing after a second double-click.
    /// </summary>
    private static bool FocusExistingInstance()
    {
        try
        {
            var self = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var other in System.Diagnostics.Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id == self.Id)
                    {
                        continue;
                    }

                    var hwnd = other.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        continue;
                    }

                    _ = ShowWindow(hwnd, SW_RESTORE);
                    _ = SetForegroundWindow(hwnd);
                    return true;
                }
            }
        }
        catch
        {
            // cosmetic only — the message box fallback still explains things
        }

        return false;
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
