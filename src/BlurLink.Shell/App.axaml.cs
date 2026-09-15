using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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
            SetupTrayIcon(window, vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Task 11 (R7): static art, live text. The tooltip mirrors the
    /// session story; the menu offers Show + Copy diagnostics + Quit. Best
    /// effort: a tray failure must never break startup.</summary>
    private void SetupTrayIcon(MainWindow window, MainViewModel vm)
    {
        try
        {
            // NOTE: the brief's `new WindowIcon("Assets/app.ico")` loads from
            // the filesystem, where the file does not exist (it is an embedded
            // AvaloniaResource) — that throws at startup. Load from the asset
            // catalog instead; same pixels, no crash.
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BlurLink/Assets/app.ico"))),
                ToolTipText = vm.TrayToolTip,
                IsVisible = true,
            };
            var menu = new NativeMenu();
            var show = new NativeMenuItem("Show BlurLink");
            show.Click += (_, _) => { window.Show(); window.Activate(); };
            var copy = new NativeMenuItem("Copy diagnostics");
            copy.Click += (_, _) => vm.CopyDiagnosticsCommand.Execute(null);
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => { window.Close(); };
            menu.Items.Add(show);
            menu.Items.Add(copy);
            menu.Items.Add(quit);
            tray.Menu = menu;
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.TrayToolTip))
                {
                    tray.ToolTipText = vm.TrayToolTip;
                }
            };
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Warn("Tray icon unavailable: " + ex.Message);
        }
    }

    private static string LogsDir() => Path.GetDirectoryName(Core.Logging.AppLog.DefaultPath()) ?? Path.GetTempPath();
}
