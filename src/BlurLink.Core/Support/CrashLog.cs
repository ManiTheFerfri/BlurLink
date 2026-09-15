namespace BlurLink.Core.Support;

/// <summary>Local crash capture. View-model level only — no packet data can
/// reach it, so the metadata-only logging discipline holds trivially.</summary>
public static class CrashLog
{
    public static string Write(string dir, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(dir, $"crash-{stamp}.log");
        var frames = (ex.StackTrace ?? "(no stack)").Split('\n').Take(20);
        File.WriteAllText(path,
            $"[{DateTime.UtcNow:O}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}" +
            string.Join(Environment.NewLine, frames));
        return path;
    }

    public static string? Newest(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, "crash-*.log")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
