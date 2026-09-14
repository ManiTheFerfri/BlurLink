using System.Collections.ObjectModel;
using BlurLink.Core.Logging;
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

    /// <summary>
    /// <paramref name="logDirectory"/> null means the real logs dir;
    /// tests pass a temp dir. <paramref name="platform"/> null gets a
    /// throwaway that throws <see cref="InvalidOperationException"/> on use
    /// (production always passes both explicitly).
    /// </summary>
    public DiagnosticsViewModel(string? logDirectory = null, IPlatformServices? platform = null)
    {
        _logDirectory = logDirectory;
        _platform = platform ?? new ThrowingPlatformServices();
        _ui = SynchronizationContext.Current;
        RefreshHelperLogCommand = new RelayCommand(_ => RefreshHelperLog());
        CopyHelperLogCommand = new RelayCommand(_ => CopyHelperLog());
        RefreshHelperLog();
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
