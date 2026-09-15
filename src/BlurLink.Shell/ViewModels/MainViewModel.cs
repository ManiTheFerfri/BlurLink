using System.IO;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Core.Config;
using BlurLink.Core.Diagnostics;
using BlurLink.Core.FirstRun;
using BlurLink.Core.Logging;
using BlurLink.Core.Session;
using BlurLink.Platform;
using BlurLink.Shell.Notifications;

namespace BlurLink.Shell.ViewModels;

public enum StatusChipKind { Idle, Running, Lost }

/// <summary>
/// Shell root: the Join, Host, Settings and Diagnostics destinations plus
/// status bar, notifications and the status chip. Port of the WPF
/// MainViewModel with the brief's three structural changes (composed ctor,
/// Notices, chip).
/// </summary>
public sealed class MainViewModel : ShellViewModelBase, IDisposable
{
    private readonly BlurLinkConfigStore _store = new();
    private readonly HelperLauncher _launcher = new();
    private readonly AdapterWatcher _watcher = new();
    private readonly IPlatformServices _platform;
    private HelperIpcClient? _ipc;
    private bool _disposed;

    public JoinViewModel Join { get; }

    public HostViewModel Host { get; }

    public SettingsViewModel Settings { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public NotificationCenter Notices { get; }

    /// <summary>Guided first run over live state (Task 8). Null-never after construction.</summary>
    public FirstRunViewModel FirstRun { get; }

    /// <summary>True once: no verified profile yet, still Research mode, not dismissed.</summary>
    public bool ShowFirstRun
        => string.IsNullOrWhiteSpace(Config.VerifiedProfileDate)
            && Config.DiscoveryUdpPort is null
            && !Config.FirstRunDismissed;

    private string _currentView = "Join";
    public string CurrentView { get => _currentView; set => Set(ref _currentView, value); }

    /// <summary>The child matching <see cref="CurrentView"/> (Join, Host, Settings or Diagnostics).</summary>
    public object? CurrentPane => CurrentView switch
    {
        "Host" => Host,
        "Settings" => Settings,
        "Diagnostics" => Diagnostics,
        "Join" => Join,
        _ => Join,
    };

    private string _statusBar = "Research mode — enter verified discovery parameters.";
    public string StatusBar { get => _statusBar; set => Set(ref _statusBar, value); }

    private string _trayToolTip = "BlurLink — idle";

    /// <summary>Task 11 (R7): mirrors the session story for the tray icon
    /// (static art, live text). Default until the first state change.</summary>
    public string TrayToolTip { get => _trayToolTip; private set => Set(ref _trayToolTip, value); }

    private string _statusChipText = "Bridge stopped";
    public string StatusChipText { get => _statusChipText; private set => Set(ref _statusChipText, value); }

    private StatusChipKind _statusChipKind = StatusChipKind.Idle;
    public StatusChipKind StatusChipKind { get => _statusChipKind; private set => Set(ref _statusChipKind, value); }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    public BlurLinkConfig Config { get; }

    // Task 11: previous story code per session VM — transitions notify once,
    // steady states never re-notify. Seeded from the live stories so startup
    // itself is not a "transition".
    private string _joinPrevCode = string.Empty;
    private string _hostPrevCode = string.Empty;

    public MainViewModel(BlurLinkConfig config, NotificationCenter notices, IPlatformServices platform)
    {
        Config = config;
        Notices = notices;
        _platform = platform;
        AppLog.Initialize(Config.LogLevel);
        AppLog.Info("BlurLink started. Research mode (port unknown until verified).");
        StageBridgeFiles();
        Join = new JoinViewModel(Config, OnConfigChanged, _launcher, GetOrConnectIpcAsync, DropHelperConnection, UpdateStatus);
        Host = new HostViewModel(Config, OnConfigChanged, _launcher, GetOrConnectIpcAsync, DropHelperConnection, UpdateStatus);
        Settings = new SettingsViewModel(Config, OnConfigChanged, platform: platform);
        Diagnostics = new DiagnosticsViewModel(platform: platform, verify: new VerifyProfileViewModel(
            Config,
            s => Join.Settings.DiscoveryPort = s,
            s => Join.Settings.BroadcastDestination = s,
            s => Join.Settings.PayloadHex = s,
            OnConfigChanged), config: Config);

        NavigateCommand = new RelayCommand(p =>
        {
            CurrentView = p?.ToString() ?? "Join";
            Diagnostics.IsVisible = CurrentView == "Diagnostics";
            RefreshAllAdapters(); // adapter may have changed while away
            Join.Session.AttachBlur(silent: true); // game may have started while away
            if (CurrentView == "Diagnostics")
            {
                Diagnostics.RefreshHelperLog(); // fresh tail when the tab opens
            }

            Raise(nameof(CurrentPane));
            AppLog.Debug("Navigate: " + CurrentView);
        });
        FirstRun = new FirstRunViewModel(
            Config, Join.Settings, BlurFinder.Candidates,
            view => NavigateCommand.Execute(view), OnConfigChanged);
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics());
        Join.Session.StateChanged += _ => UpdateStatusChip();
        Join.Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(JoinSessionViewModel.BridgeRunning) or nameof(JoinSessionViewModel.HelperLost))
            {
                UpdateStatusChip();
            }
        };
        // Task 11: one notice per story transition on each session VM (both
        // already forward their coordinator event — code is truth, no new
        // passthroughs needed) plus the live tray tooltip.
        _joinPrevCode = Join.Session.Story.Code;
        _hostPrevCode = Host.Story.Code;
        Join.Session.StateChanged += OnJoinStateChanged;
        Host.StateChanged += OnHostStateChanged;
        _watcher.Changed += RefreshAllAdapters;
    }

    /// <summary>Task 11: per-profile host memory (R8). The dict key is the
    /// verified profile name, or "Research mode" when none is active.</summary>
    internal static string ActiveProfileName(BlurLinkConfig config)
        => string.IsNullOrWhiteSpace(config.VerifiedProfileName)
            ? "Research mode"
            : config.VerifiedProfileName.Trim();

    /// <summary>Task 11: remember the current host IP under the active profile
    /// (start-time hook: both session VMs call this after a successful start).</summary>
    internal static void RememberHost(BlurLinkConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.HostOverlayIp))
        {
            return;
        }

        config.HostIpByProfile[ActiveProfileName(config)] = config.HostOverlayIp.Trim();
    }

    /// <summary>Task 11: the host IP remembered for the active profile, if any
    /// (verify-apply hook: the Task 8/9 writers prefill the Join field from this).</summary>
    internal static string? RecallHost(BlurLinkConfig config)
        => config.HostIpByProfile.TryGetValue(ActiveProfileName(config), out var ip)
            && !string.IsNullOrWhiteSpace(ip)
            ? ip
            : null;

    private void OnJoinStateChanged(SessionState state) => PushTransitionNotice(ref _joinPrevCode, state);

    private void OnHostStateChanged(SessionState state) => PushTransitionNotice(ref _hostPrevCode, state);

    private void PushTransitionNotice(ref string prevCode, SessionState state)
    {
        var story = SessionStoryTable.Describe(state);
        var notice = SessionNotifier.Watch(prevCode, story.Code);
        prevCode = story.Code;
        if (notice is not null)
        {
            Notices.Push(notice);
        }

        TrayToolTip = $"BlurLink — {story.Headline}";
    }

    private void UpdateStatusChip()
    {
        if (Join.Session.HelperLost)
        {
            StatusChipText = "Helper lost";
            StatusChipKind = StatusChipKind.Lost;
        }
        else if (Join.Session.BridgeRunning)
        {
            StatusChipText = "Bridge running";
            StatusChipKind = StatusChipKind.Running;
        }
        else
        {
            StatusChipText = "Bridge stopped";
            StatusChipKind = StatusChipKind.Idle;
        }
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
            Join.Settings.RefreshAdaptersCommand.Execute(null);
            Host.RefreshAdaptersCommand.Execute(null);
            Settings.RefreshAdaptersCommand.Execute(null);
            AppLog.Debug($"Adapters refreshed (join={Join.Settings.Adapters.Count}, host={Host.Adapters.Count}).");
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
        FirstRun.Refresh();
        Raise(nameof(ShowFirstRun));
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
                    $"  running={Join.Session.BridgeRunning} helperLost={Join.Session.HelperLost}" + Environment.NewLine +
                    $"  filter={Join.Session.ActiveFilter}" + Environment.NewLine +
                    $"  {Join.Session.Counters}" + Environment.NewLine;
            text += Environment.NewLine + "[Host mode]" + Environment.NewLine +
                    $"  running={Host.HostRunning} players={Host.Players.Count}" + Environment.NewLine +
                    $"  {Host.Counters}" + Environment.NewLine +
                    $"  {Host.StatusText}" + Environment.NewLine;
            text += Environment.NewLine + "[Shell]" + Environment.NewLine +
                    $"  shell=Avalonia (BlurLink.Shell)" + Environment.NewLine;
            _platform.CopyToClipboard(text);
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
        Host.Dispose();
        Join.Dispose();
        Diagnostics.Dispose();
    }
}
