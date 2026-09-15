using System.Collections.ObjectModel;
using BlurLink.Contracts;
using BlurLink.Core.Config;
using BlurLink.Core.Diagnostics;
using BlurLink.Core.Logging;
using BlurLink.Core.Support;
using BlurLink.Platform;

namespace BlurLink.Shell.ViewModels;

/// <summary>Diagnostics destination: the helper-log viewer that used to live
/// on the WPF Settings tab (spec §7.3). Owns the tail, the status line, the
/// 2s auto-refresh timer, and the copy-to-clipboard action. Append-only
/// constructor: later tasks add optional parameters at the end only.</summary>
public sealed class DiagnosticsViewModel : ShellViewModelBase, IDisposable
{
    private readonly string? _logDirectory;
    private readonly IPlatformServices _platform;
    private readonly BlurLinkConfig? _config;
    private readonly SynchronizationContext? _ui;
    private bool _disposed;

    /// <summary>Helper log tail shown in the diagnostics viewer.</summary>
    public ObservableCollection<string> HelperLogLines { get; } = new();

    private string _helperLogStatus = "Helper log: nothing read yet.";
    public string HelperLogStatus { get => _helperLogStatus; set => Set(ref _helperLogStatus, value); }

    private bool _autoRefreshHelperLog = true;
    public bool AutoRefreshHelperLog
    {
        get => _autoRefreshHelperLog;
        set { if (Set(ref _autoRefreshHelperLog, value)) Raise(nameof(AutoRefreshHelperLog)); }
    }

    private System.Threading.Timer? _helperLogTimer;
    private int _helperLogRefreshBusy;

    /// <summary>Whether the diagnostics pane is the active view (set by MainVM).</summary>
    public bool IsVisible { get; set; }

    /// <summary>The helper log file being tailed.</summary>
    public string LogPath => FindHelperLogPath();

    public RelayCommand RefreshHelperLogCommand { get; }
    public RelayCommand CopyHelperLogCommand { get; }
    public RelayCommand BundleCommand { get; }

    private string _bundleResult = "No support bundle created yet.";
    /// <summary>Last support-bundle outcome: the zip path or the error.</summary>
    public string BundleResult { get => _bundleResult; set => Set(ref _bundleResult, value); }

    private string _crashTail = "No crashes recorded.";
    /// <summary>Newest crash file tail-20, or "No crashes recorded.".</summary>
    public string CrashTail { get => _crashTail; set => Set(ref _crashTail, value); }

    /// <summary>The Verify section child (Task 9). Null in older constructions
    /// and tests that pass nothing — the view hides the section then.</summary>
    public VerifyProfileViewModel? Verify { get; }

    /// <summary>Whether the Verify section is present (bound by the view).</summary>
    public bool HasVerify => Verify is not null;

    /// <summary>
    /// <paramref name="logDirectory"/> null means the real logs dir;
    /// tests pass a temp dir. <paramref name="platform"/> null gets a
    /// throwaway that throws <see cref="InvalidOperationException"/> on use
    /// (production always passes both explicitly).
    /// <paramref name="verify"/> null hides the Verify section (older
    /// constructions, including the Task 6 tests, pass nothing).
    /// <paramref name="config"/> null leaves the bundle command reporting
    /// "configuration unavailable" (older constructions pass nothing).
    /// </summary>
    public DiagnosticsViewModel(string? logDirectory = null, IPlatformServices? platform = null, VerifyProfileViewModel? verify = null, BlurLinkConfig? config = null)
    {
        _logDirectory = logDirectory;
        _platform = platform ?? new ThrowingPlatformServices();
        Verify = verify;
        _config = config;
        _ui = SynchronizationContext.Current;
        RefreshHelperLogCommand = new RelayCommand(_ => RefreshHelperLog());
        CopyHelperLogCommand = new RelayCommand(_ => CopyHelperLog());
        BundleCommand = new RelayCommand(_ => CreateBundle());
        RefreshHelperLog();
        RefreshCrashTail();
        StartHelperLogTimer();
    }

    private sealed class ThrowingPlatformServices : IPlatformServices
    {
        public void CopyToClipboard(string text) => throw new InvalidOperationException("No IPlatformServices was provided.");
        public Task<string?> PickExeFileAsync(string initialPath) => throw new InvalidOperationException("No IPlatformServices was provided.");
        public void OpenFolder(string path) => throw new InvalidOperationException("No IPlatformServices was provided.");
    }

    private void StartHelperLogTimer()
    {
        if (_helperLogTimer is not null)
        {
            return;
        }

        // Auto-refresh every 2s: reads are cheap, bounded end-seek tails.
        // The tick is skipped while a previous read runs or the tab is hidden.
        _helperLogTimer = new System.Threading.Timer(_ =>
        {
            if (_disposed || !AutoRefreshHelperLog || !IsVisible)
            {
                return;
            }

            if (Interlocked.Exchange(ref _helperLogRefreshBusy, 1) == 1)
            {
                return;
            }

            try
            {
                _ui?.Post(_ =>
                {
                    if (!_disposed)
                    {
                        RefreshHelperLog();
                    }
                }, null);
            }
            finally
            {
                Interlocked.Exchange(ref _helperLogRefreshBusy, 0);
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private string FindHelperLogPath()
        => _logDirectory is null
            ? HelperLauncher.DefaultHelperLogPath()
            : Path.Combine(_logDirectory, "helper.log");

    public void RefreshHelperLog()
    {
        try
        {
            var path = FindHelperLogPath();
            var lines = LogTail.Read(path, maxLines: 200);
            HelperLogLines.Clear();
            foreach (var line in lines)
            {
                HelperLogLines.Add(line);
            }

            HelperLogStatus = lines.Count == 0
                ? $"No helper log yet ({path}). It appears after the first bridge start."
                : $"{path} — last {lines.Count} line(s), newest last.";
        }
        catch (Exception ex)
        {
            HelperLogStatus = "Could not read helper log: " + ex.Message;
        }
    }

    private void CopyHelperLog()
    {
        try
        {
            var path = FindHelperLogPath();
            var text = LogTail.FormatForDiagnostics(path, LogTail.Read(path, maxLines: 200));
            _platform.CopyToClipboard(text);
            HelperLogStatus = "Helper log copied to the clipboard (metadata only).";
        }
        catch (Exception ex)
        {
            HelperLogStatus = "Copy failed: " + ex.Message;
        }
    }

    private string FindLogsDir()
        => _logDirectory
            ?? Path.GetDirectoryName(AppLog.DefaultPath())
            ?? Path.GetTempPath();

    private void CreateBundle()
    {
        try
        {
            if (_config is null)
            {
                BundleResult = "configuration unavailable";
                return;
            }

            var text = DiagnosticsCollector.BuildDiagnosticsText(
                _config.HostOverlayIp, _config.DiscoveryUdpPort, _config.SelectedAdapterIfIndex);
            var json = BlurLinkConfigStore.ExportJson(_config);
            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlurLink", "bundles");
            BundleResult = SupportBundle.Create(FindLogsDir(), text, json, destDir);
        }
        catch (Exception ex)
        {
            BundleResult = "Bundle failed: " + ex.Message;
        }
    }

    public void RefreshCrashTail()
    {
        try
        {
            var newest = CrashLog.Newest(FindLogsDir());
            if (newest is null)
            {
                CrashTail = "No crashes recorded.";
                return;
            }

            var lines = LogTail.Read(newest, maxLines: 20);
            CrashTail = lines.Count == 0
                ? $"{Path.GetFileName(newest)} (empty)."
                : $"{Path.GetFileName(newest)}:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }
        catch (Exception ex)
        {
            CrashTail = "Could not read crash log: " + ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _helperLogTimer?.Dispose();
        _helperLogTimer = null;
    }
}
