using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BlurLink.Core.Config;
using BlurLink.Core.Support;
using BlurLink.Platform;
using BlurLink.Shell.Notifications;
using BlurLink.Shell.Services;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;

namespace BlurLink.Shell;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { CrashLog.Write(LogsDir(), e.ExceptionObject as Exception ?? new Exception("unknown")); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { CrashLog.Write(LogsDir(), e.Exception); } catch { }
            e.SetObserved();
        };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            try { CrashLog.Write(LogsDir(), e.Exception); } catch { }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Embedded bridge files live in this assembly (Native/* from M4);
            // point the launcher at them explicitly — it resolves against the
            // Platform assembly by default, where Shell names match nothing.
            HelperLauncher.ConfigureResources(typeof(App).Assembly, HelperLauncher.ShellEmbeddedFiles);

            var store = new BlurLinkConfigStore();
            var config = store.Load();
            MainWindow? window = null;
            var platform = new WindowsPlatformServices(() => (TopLevel?)window);
            var vm = new MainViewModel(config, new NotificationCenter(), platform);
            window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string LogsDir() => Path.GetDirectoryName(Core.Logging.AppLog.DefaultPath()) ?? Path.GetTempPath();
}
