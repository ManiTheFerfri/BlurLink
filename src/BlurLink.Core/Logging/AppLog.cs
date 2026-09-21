using BlurLink.Contracts;

namespace BlurLink.Core.Logging;

/// <summary>
/// Minimal metadata-only file logger. There is deliberately no payload
/// parameter anywhere in this API, so packet contents cannot be logged by
/// construction. Logs roll by size (1 MB, 3 generations).
/// Location: %LocalAppData%\BlurLink\logs\blurlink.log
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string _path = DefaultPath();
    private static string _level = BlurLinkConstants.DefaultLogLevel;
    private static bool _initialized;

    private static readonly string[] Levels = BlurLinkConstants.LogLevels;

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlurLink", "logs", "blurlink.log");

    public static string CurrentPath
    {
        get { lock (Gate) { return _path; } }
    }

    public static void Initialize(string level, string? path = null)
    {
        lock (Gate)
        {
            _level = BlurLinkConstants.NormalizeLogLevel(level);
            _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                _initialized = true;
            }
            catch
            {
                // Never throw from logging setup: run silent instead.
                _initialized = false;
            }
        }
    }

    public static void Info(string message) => Write("Information", message);
    public static void Warn(string message) => Write("Warning", message);
    public static void Error(string message) => Write("Error", message);
    public static void Debug(string message) => Write("Debug", message);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                return;
            }

            if (Array.IndexOf(Levels, level) > Array.IndexOf(Levels, _level))
            {
                return;
            }

            try
            {
                RollIfNeeded();
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < 1024 * 1024)
            {
                return;
            }

            for (var i = 2; i >= 1; i--)
            {
                var src = i == 1 ? _path : $"{_path}.{i - 1}";
                var dst = $"{_path}.{i}";
                if (File.Exists(src))
                {
                    File.Move(src, dst, overwrite: true);
                }
            }
        }
        catch
        {
            // best effort
        }
    }
}
