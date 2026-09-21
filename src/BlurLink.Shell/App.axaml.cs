using System.ComponentModel;
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
    /// effort: a tray failure must never break startup. The icons live in a
    /// field so <see cref="DisposeTrayIcon"/> can hide and release them and
    /// drop the tooltip subscription on lifetime exit / window close.</summary>
    private TrayIcons? _trayIcons;
    private MainViewModel? _trayViewModel;
    private PropertyChangedEventHandler? _trayTooltipHandler;

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
            _trayIcons = new TrayIcons { tray };
            TrayIcon.SetIcons(this, _trayIcons);
            _trayViewModel = vm;
            _trayTooltipHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.TrayToolTip))
                {
                    tray.ToolTipText = vm.TrayToolTip;
                }
            };
            vm.PropertyChanged += _trayTooltipHandler;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Exit += OnAppExit;
            }

            window.Closed += OnMainWindowClosed;
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Warn("Tray icon unavailable: " + ex.Message);
        }
    }

    private void OnAppExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => DisposeTrayIcon();

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Closed -= OnMainWindowClosed;
        }

        DisposeTrayIcon();
    }

    /// <summary>Drops the tooltip subscription and hides/releases the tray
    /// icons. Runs on lifetime exit and on main-window close (either may come
    /// first); re-entry is a no-op.</summary>
    private void DisposeTrayIcon()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit -= OnAppExit;
        }

        if (_trayViewModel is not null && _trayTooltipHandler is not null)
        {
            _trayViewModel.PropertyChanged -= _trayTooltipHandler;
        }

        _trayViewModel = null;
        _trayTooltipHandler = null;
        if (_trayIcons is not null)
        {
            foreach (var icon in _trayIcons)
            {
                icon.Dispose();
            }

            _trayIcons.Clear();
            _trayIcons = null;
        }
    }

    private static string LogsDir() => Path.GetDirectoryName(Core.Logging.AppLog.DefaultPath()) ?? Path.GetTempPath();
}
