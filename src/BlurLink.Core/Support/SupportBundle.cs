using System.IO.Compression;

namespace BlurLink.Core.Support;

/// <summary>One-click support bundle: logs + diagnostics metadata, zipped.
/// Only *.log files are ever collected (plus the three generated files), so a
/// stray capture placed in the logs dir cannot enter a bundle by construction.</summary>
public static class SupportBundle
{
    public static string Create(string logsDir, string diagnosticsText, string configJson, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(destDir, $"support-bundle-{stamp}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "*.log").OrderBy(f => f))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        AddText(zip, "diagnostics.txt", diagnosticsText);
        AddText(zip, "settings.json", configJson);
        AddText(zip, "versions.txt",
            $"BlurLink={typeof(SupportBundle).Assembly.GetName().Version} " +
            $"os={Environment.OSVersion} x64={Environment.Is64BitProcess}");
        return zipPath;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
