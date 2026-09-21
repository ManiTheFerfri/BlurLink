using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Shell.Notifications;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Shell.Tests;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void EveryView_PassesTheAccessibilityAudit()
    {
        using var app = TestShellApp.Create(); // real MainViewModel, verified profile set so no first-run overlay
        var violations = new List<string>();
        foreach (var view in new Visual[] { app.Join, app.Host, app.Settings, app.Diagnostics, app.FirstRun, app.Main })
        {
            violations.AddRange(UiAudit.Audit(view));
        }

        Assert.Empty(violations);
    }

    [AvaloniaFact]
    public void Navigation_ReachesAllThreeDestinations()
    {
        using var app = TestShellApp.Create();

        foreach (var view in new[] { "Join", "Settings", "Diagnostics" })
        {
            app.Main.Navigate(view);
            Assert.Equal(view, app.Vm.CurrentView);
        }
    }
}

/// <summary>Task 13: the whole shell under one headless roof — the real
/// <see cref="MainViewModel"/> ctor <c>(BlurLinkConfig, NotificationCenter,
/// IPlatformServices)</c> matches the brief exactly, so no deviation;
/// <see cref="FakePlatformServices"/> is reused from
/// SettingsHeadlessTests.cs.</summary>
internal sealed class TestShellApp : IDisposable
{
    public MainViewModel Vm { get; }
    public JoinView Join { get; }
    public HostView Host { get; }
    public SettingsView Settings { get; }
    public DiagnosticsView Diagnostics { get; }
    public FirstRunView FirstRun { get; }
    public MainWindow Main { get; }

    private readonly List<Window> _hosts = new();

    private TestShellApp(MainViewModel vm)
    {
        Vm = vm;
        Join = ShowInHost(new JoinView { DataContext = vm.Join });
        Host = ShowInHost(new HostView { DataContext = vm.Host });
        Settings = ShowInHost(new SettingsView { DataContext = vm.Settings });
        Diagnostics = ShowInHost(new DiagnosticsView { DataContext = vm.Diagnostics });
        FirstRun = ShowInHost(new FirstRunView { DataContext = vm.FirstRun });
        Main = new MainWindow { DataContext = vm };
        Main.Show();
    }

    private T ShowInHost<T>(T view)
        where T : Control
    {
        var window = new Window { Content = view };
        window.Show();
        _hosts.Add(window);
        return view;
    }

    public static TestShellApp Create()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.VerifiedProfileDate = "2026-09-14"; // hide the first-run overlay
        var vm = new MainViewModel(
            config,
            new NotificationCenter(),
            new FakePlatformServices());
        return new TestShellApp(vm);
    }

    public void Dispose()
    {
        Main.Close();
        foreach (var host in _hosts)
        {
            host.Close();
        }

        Vm.Dispose();
    }
}
