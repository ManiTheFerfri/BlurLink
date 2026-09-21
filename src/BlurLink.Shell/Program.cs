using Avalonia;
using System;
using System.Diagnostics;
using BlurLink.Core.Logging;
using BlurLink.Platform;

namespace BlurLink.Shell;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
        {
            AppLog.Error("BlurLink supports Windows 10/11 x64 only.");
            return 2;
        }

        using var singleInstance = SingleInstanceGuard.Acquire();
        if (!singleInstance.IsAcquired)
        {
            FocusExistingInstance();
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void FocusExistingInstance()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id == self.Id || other.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    _ = ShowWindow(other.MainWindowHandle, 9);
                    _ = SetForegroundWindow(other.MainWindowHandle);
                    return;
                }
            }
        }
        catch
        {
            // cosmetic only — the running window stays where it is
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr nWnd, int nCmdShow);
}
