using System.Diagnostics;
using System.IO;

namespace BlurLink.Desktop.Services;

/// <summary>
/// Tracks running game instances for status + auto-stop. Robustness rules:
/// - watches EVERY live instance, not just one (launcher stubs that exit
///   immediately no longer cause false "Blur exited");
/// - matches the configured exe path when known, name otherwise;
/// - "running" means at least one watched instance is alive; the Exited
///   event fires only when the last one ends;
/// - callers re-scan on a timer so games started later are picked up.
/// Process-object identity is used throughout: no PID-reuse hazard.
/// </summary>
public sealed class BlurProcessWatcher : IDisposable
{
    private readonly SynchronizationContext? _ui;
    private readonly Dictionary<int, Process> _watched = new();
    private readonly object _gate = new();
    private bool _disposed;

    public event Action? Exited;

    public bool Watching
    {
        get
        {
            lock (_gate)
            {
                PruneExitedLocked();
                return _watched.Count > 0;
            }
        }
    }

    public int WatchedCount
    {
        get
        {
            lock (_gate)
            {
                PruneExitedLocked();
                return _watched.Count;
            }
        }
    }

    /// <summary>Newest watched PID, for status display. 0 when idle.</summary>
    public int PrimaryPid
    {
        get
        {
            lock (_gate)
            {
                PruneExitedLocked();
                return _watched.Count == 0 ? 0 : _watched.Keys.Max();
            }
        }
    }

    public DateTime StartedUtc { get; private set; }

    public bool HasExitInfo { get; private set; }

    public int LastExitCode { get; private set; }

    public TimeSpan LastRunDuration { get; private set; }

    public BlurProcessWatcher()
    {
        _ui = SynchronizationContext.Current;
    }

    /// <summary>Watch one process exclusively (a game we launched).</summary>
    public void Watch(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            DetachAllLocked();
            AddLocked(process, resetClock: true);
        }
    }

    /// <summary>
    /// Attaches to every live instance of the game (user started it).
    /// Prefers instances whose exe matches <paramref name="expectedPath"/>.
    /// Returns true if at least one instance is watched afterwards.
    /// Never starts anything.
    /// </summary>
    public bool AttachToRunning(string processName, string? expectedPath = null)
    {
        try
        {
            var candidates = Process.GetProcessesByName(processName);
            var attachedAny = false;
            lock (_gate)
            {
                var scored = new List<(Process Proc, DateTime Started, bool PathMatch)>();
                foreach (var p in candidates)
                {
                    try
                    {
                        if (p.HasExited)
                        {
                            continue;
                        }

                        var started = SafeStartTime(p);
                        scored.Add((p, started, PathMatches(p, expectedPath)));
                    }
                    catch
                    {
                        // unreadable instance: still a live candidate by name
                        scored.Add((p, DateTime.MinValue, false));
                    }
                }

                // Path matches first, then newest — so a same-named unrelated
                // process never shadows the real game.
                foreach (var (proc, _, _) in scored
                             .OrderByDescending(s => s.PathMatch)
                             .ThenByDescending(s => s.Started))
                {
                    try
                    {
                        if (proc.HasExited || _watched.ContainsKey(proc.Id))
                        {
                            continue;
                        }

                        AddLocked(proc, resetClock: _watched.Count == 0);
                        attachedAny = true;
                    }
                    catch
                    {
                        // raced an exit; keep the rest
                    }
                }

                return attachedAny || _watched.Count > 0;
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Debug("Attach scan failed: " + ex.Message);
            return Watching;
        }
    }

    private void AddLocked(Process process, bool resetClock)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += OnExited;
        }
        catch
        {
            // Event hookup failed; polling via Watching still sees it until exit.
        }

        _watched[process.Id] = process;
        if (resetClock)
        {
            HasExitInfo = false;
            StartedUtc = DateTime.UtcNow;
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        bool emptied = false;
        int code = -1;
        lock (_gate)
        {
            if (sender is Process proc)
            {
                try
                {
                    code = proc.HasExited ? proc.ExitCode : -1;
                }
                catch
                {
                    code = -1;
                }

                if (_watched.Remove(proc.Id))
                {
                    try
                    {
                        proc.Exited -= OnExited;
                    }
                    catch
                    {
                        // best effort
                    }
                }

                emptied = _watched.Count == 0;
            }
        }

        if (!emptied)
        {
            return; // other instances still running; stay attached
        }

        LastExitCode = code;
        LastRunDuration = DateTime.UtcNow - StartedUtc;
        HasExitInfo = true;
        if (_ui is not null)
        {
            _ui.Post(_ => Exited?.Invoke(), null);
        }
        else
        {
            Exited?.Invoke();
        }
    }

    private void PruneExitedLocked()
    {
        List<int> dead = new();
        foreach (var (id, proc) in _watched)
        {
            bool gone;
            try
            {
                gone = proc.HasExited;
            }
            catch
            {
                gone = true;
            }

            if (gone)
            {
                dead.Add(id);
            }
        }

        foreach (var id in dead)
        {
            try
            {
                _watched[id].Exited -= OnExited;
            }
            catch
            {
                // best effort
            }

            _watched.Remove(id);
        }
    }

    private static DateTime SafeStartTime(Process p)
    {
        try
        {
            return p.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool PathMatches(Process p, string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath))
        {
            return false;
        }

        try
        {
            var actual = p.MainModule?.FileName;
            return !string.IsNullOrEmpty(actual) &&
                string.Equals(
                    Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // unreadable path: name match is all we have
        }
    }

    private void DetachAllLocked()
    {
        foreach (var proc in _watched.Values)
        {
            try
            {
                proc.Exited -= OnExited;
            }
            catch
            {
                // best effort
            }
        }

        _watched.Clear();
    }

    public void Stop()
    {
        lock (_gate)
        {
            DetachAllLocked();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
