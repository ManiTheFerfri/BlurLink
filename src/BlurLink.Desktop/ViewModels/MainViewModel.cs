using System.IO;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Config;
using BlurLink.Core.Diagnostics;
using BlurLink.Core.Logging;
using BlurLink.Platform;

namespace BlurLink.Desktop.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly BlurLinkConfigStore _store = new();
    private readonly HelperLauncher _launcher = new();
    private readonly AdapterWatcher _watcher = new();
    private HelperIpcClient? _ipc;
    private bool _disposed;

    public JoinViewModel Join { get; }
    public HostViewModel Host { get; }
    public SettingsViewModel Settings { get; }

    private string _currentView = "Join";
    public string CurrentView { get => _currentView; set => Set(ref _currentView, value); }

    private string _statusBar = "Research mode — enter verified discovery parameters.";
    public string StatusBar { get => _statusBar; set => Set(ref _statusBar, value); }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    public BlurLinkConfig Config { get; private set; }

    public MainViewModel()
    {
        Config = _store.Load();
        AppLog.Initialize(Config.LogLevel);
        AppLog.Info("BlurLink started. Research mode (port unknown until verified).");
        StageBridgeFiles();
        Join = new JoinViewModel(Config, OnConfigChanged, _launcher, GetOrConnectIpcAsync, DropHelperConnection, UpdateStatus);
        Host = new HostViewModel(Config, OnConfigChanged, _launcher, GetOrConnectIpcAsync, DropHelperConnection, UpdateStatus);
        Settings = new SettingsViewModel(Config, OnConfigChanged);

        NavigateCommand = new RelayCommand(p =>
        {
            CurrentView = p?.ToString() ?? "Join";
            Settings.IsSettingsTabVisible = CurrentView == "Settings";
            RefreshAllAdapters(); // adapter may have changed while away
            Host.RefreshAdapters(); // host mode's picker needs the same
            Join.AttachBlur(silent: true); // game may have started while away
            if (CurrentView == "Settings")
            {
                Settings.RefreshHelperLog(); // fresh tail when the tab opens
            }

            AppLog.Debug("Navigate: " + CurrentView);
        });
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics());
        _watcher.Changed += RefreshAllAdapters;
    }

    /// <summary>First launch: put helper + driver where they belong.</summary>
    private static void StageBridgeFiles()
    {
        try
        {
            if (HelperLauncher.HasEmbeddedBridgeFiles)
            {
                var helper = HelperLauncher.EnsureStagedBinaries();
                AppLog.Info("Bridge files staged at: " + helper);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Staging bridge files failed: " + ex.Message);
        }
    }

    public void RefreshAllAdapters()
    {
        try
        {
            Join.RefreshAdaptersCommand.Execute(null);
            Host.RefreshAdaptersCommand.Execute(null);
            Settings.RefreshAdaptersCommand.Execute(null);
            AppLog.Debug($"Adapters refreshed (join={Join.Adapters.Count}, host={Host.Adapters.Count}).");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Adapter refresh failed: " + ex.Message);
        }
    }

    private void OnConfigChanged()
    {
        _store.Save(Config);
        Join.RefreshFromConfig();
        Host.RefreshFromConfig();
        Settings.RefreshFromConfig();
    }

    private void UpdateStatus(string s) => StatusBar = s;

    public void DropHelperConnection()
    {
        _ipc?.Dispose();
        _ipc = null;
    }

    private async Task<HelperIpcClient> GetOrConnectIpcAsync(Func<bool> stillAlive, CancellationToken ct, int timeoutSeconds)
    {
        if (_ipc is { Connected: true })
        {
            return _ipc;
        }

        _ipc?.Dispose();
        _ipc = new HelperIpcClient();
        await _ipc.ConnectAsync(_launcher.PipeName, ct, stillAlive, timeoutSeconds).ConfigureAwait(false);

        // Protocol handshake: the helper refuses every command until a hello
        // with a matching protocolVersion arrives, so version skew fails
        // with one explicit error instead of confusing per-command failures.
        var hello = new IpcHelloMessage { Token = _launcher.Token };
        var response = await _ipc.SendAsync(hello, CancellationToken.None).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response);
        if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
            typeEl.GetString() == IpcMessageTypes.Error)
        {
            var detail = doc.RootElement.TryGetProperty("message", out var msgEl)
                ? msgEl.GetString()
                : "handshake failed";
            throw new IOException("Helper protocol handshake failed: " + detail);
        }

        return _ipc;
    }

    private void CopyDiagnostics()
    {
        try
        {
            var text = DiagnosticsCollector.BuildDiagnosticsText(
                Config.HostOverlayIp, Config.DiscoveryUdpPort, Config.SelectedAdapterIfIndex);
            text += Environment.NewLine + "[Bridge]" + Environment.NewLine +
                    $"  running={Join.BridgeRunning} helperLost={Join.HelperLost}" + Environment.NewLine +
                    $"  filter={Join.ActiveFilter}" + Environment.NewLine +
                    $"  {Join.Counters}" + Environment.NewLine;
            text += Environment.NewLine + "[Host mode]" + Environment.NewLine +
                    $"  running={Host.HostRunning} players={Host.Players.Count}" + Environment.NewLine +
                    $"  {Host.Counters}" + Environment.NewLine +
                    $"  {Host.StatusText}" + Environment.NewLine;
            System.Windows.Clipboard.SetText(text);
            StatusBar = "Diagnostics copied (metadata only).";
            AppLog.Info("Diagnostics copied.");
        }
        catch (Exception ex)
        {
            StatusBar = "Copy failed: " + ex.Message;
            AppLog.Error("Copy diagnostics failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.Dispose();
        _ipc?.Dispose();
        _launcher.Dispose();
        Settings.Dispose();
        Host.Dispose();
        Join.Dispose();
    }
}
