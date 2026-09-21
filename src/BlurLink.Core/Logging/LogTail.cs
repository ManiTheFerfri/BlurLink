using System.Text;

namespace BlurLink.Core.Logging;

/// <summary>
/// Reads the tail (last N lines) of a log file for the diagnostics viewer.
/// Metadata only by construction: it just reads whatever file it is given —
/// both blurlink.log and the helper's helper.log contain no packet payloads.
/// Seeks from the end of the file, so cost is bounded regardless of file size;
/// no locks are taken on the file so the writer (elevated helper) is never
/// blocked. Never throws: missing/locked/empty files yield an empty tail.
/// </summary>
public static class LogTail
{
    /// <summary>Reads up to <paramref name="maxLines"/> last lines of the file.</summary>
    public static IReadOnlyList<string> Read(string path, int maxLines = 200)
    {
        if (maxLines < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Array.Empty<string>();
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return Array.Empty<string>();
            }

            // Bound the scan: the tail we need is never longer than a small
            // window near the end (log lines are short). 256 KB covers far
            // more than maxLines even with pathological 1024-char lines.
            const int MaxScanBytes = 256 * 1024;
            var scanStart = Math.Max(0, stream.Length - MaxScanBytes);
            stream.Seek(scanStart, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var text = reader.ReadToEnd();

            // Drop a possibly-partial first line — but only when the scan
            // really started mid-file; a small file read whole has none.
            if (scanStart > 0)
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0)
                {
                    text = text[(firstBreak + 1)..];
                }
            }

            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (lines.Length > 0 && lines[^1].Length == 0)
            {
                lines = lines[..^1]; // trailing newline, not a real line
            }

            if (lines.Length <= maxLines)
            {
                return lines;
            }

            var tail = new string[maxLines];
            Array.Copy(lines, lines.Length - maxLines, tail, 0, maxLines);
            return tail;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            // Missing, locked, or unreadable file: an empty tail, never a crash.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Formats a tail for clipboard/paste: newest line last, with a header
    /// naming the file and a note that logs are metadata-only.
    /// </summary>
    public static string FormatForDiagnostics(string path, IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- {Path.GetFileName(path)} (metadata only, newest last) ---");
        if (lines.Count == 0)
        {
            sb.AppendLine("(no log lines found)");
        }
        else
        {
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }
}
