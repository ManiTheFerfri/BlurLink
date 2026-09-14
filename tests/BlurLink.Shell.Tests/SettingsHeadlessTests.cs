using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlurLink.Contracts;
using BlurLink.Platform;
using BlurLink.Shell.ViewModels;
using BlurLink.Shell.Views;
using Xunit;

namespace BlurLink.Shell.Tests;

/// <summary>Test-only <see cref="IPlatformServices"/> (later tasks reuse it
/// from this file, do not redefine).</summary>
internal sealed class FakePlatformServices : IPlatformServices
{
    public List<string> Copied { get; } = new();
    public string? PickResult { get; set; }
    public List<string> OpenedFolders { get; } = new();

    public void CopyToClipboard(string text) => Copied.Add(text);
    public Task<string?> PickExeFileAsync(string initialPath) => Task.FromResult(PickResult);
    public void OpenFolder(string path) => OpenedFolders.Add(path);
}

public sealed class SettingsHeadlessTests
{
    [AvaloniaFact]
    public void Export_ThenImport_RoundTripsTheHostIp()
    {
        var services = new FakePlatformServices();
        var config = BlurLinkConfig.CreateDefault();
        var saved = 0;
        var vm = new SettingsViewModel(config, () => saved++, _ => { }, services);
        config.HostOverlayIp = "10.0.0.10";

        vm.ExportCommand.Execute(null);
        var text = vm.ExportText;
        Assert.Contains("10.0.0.10", text);

        config.HostOverlayIp = string.Empty;
        vm.ImportCommand.Execute(null);

        Assert.Equal("10.0.0.10", config.HostOverlayIp);
        Assert.Equal("Configuration imported.", vm.Message);
    }

    [AvaloniaFact]
    public void UnknownLogLevel_NormalizesOnSave()
    {
        var services = new FakePlatformServices();
        string? applied = null;
        var config = BlurLinkConfig.CreateDefault();
        var vm = new SettingsViewModel(config, () => { }, level => applied = level, services);
        vm.LogLevel = "Verbose";

        vm.SaveCommand.Execute(null);

        Assert.Equal(BlurLinkConstants.DefaultLogLevel, config.LogLevel);
        Assert.Equal(BlurLinkConstants.DefaultLogLevel, applied);
    }

    [AvaloniaFact]
    public void SettingsView_Renders()
    {
        var services = new FakePlatformServices();
        var vm = new SettingsViewModel(BlurLinkConfig.CreateDefault(), () => { }, _ => { }, services);
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Assert.NotNull(view.FindControl<Button>("SaveButton"));
        }
        finally
        {
            window.Close();
        }
    }
}
