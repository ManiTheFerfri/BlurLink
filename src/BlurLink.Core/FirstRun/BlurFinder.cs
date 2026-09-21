using System.Diagnostics;

namespace BlurLink.Core.FirstRun;

/// <summary>Best-effort Blur.exe location candidates for first run step 1.
/// Order: running process, Steam library scan, registry Uninstall scan.
/// Returns paths that exist right now; empty is a normal answer (Browse covers it).</summary>
public static class BlurFinder
{
    public static IReadOnlyList<string> Candidates()
    {
        var found = new List<string>();
        try { found.AddRange(FromRunningProcess()); } catch { }
        try { found.AddRange(FromSteamLibraries()); } catch { }
        try { found.AddRange(FromUninstallRegistry()); } catch { }
        return found.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FromRunningProcess()
    {
        foreach (var p in Process.GetProcessesByName("Blur"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            finally { p.Dispose(); }
            if (!string.IsNullOrEmpty(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> FromSteamLibraries()
    {
        foreach (var steamDir in SteamDirs())
        {
            var common = Path.Combine(steamDir, "steamapps", "common");
            if (!Directory.Exists(common))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(common))
            {
                var candidate = Path.Combine(dir, "Blur.exe");
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> SteamDirs()
    {
        yield return @"C:\Program Files (x86)\Steam";
        yield return @"C:\Program Files\Steam";
        var fromRegistry = RegistrySteamPath();
        if (!string.IsNullOrWhiteSpace(fromRegistry))
        {
            yield return fromRegistry;
        }
    }

    private static string? RegistrySteamPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> FromUninstallRegistry()
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            string[] subkeys;
            try { subkeys = hive.OpenSubKey(uninstall)?.GetSubKeyNames() ?? Array.Empty<string>(); }
            catch { continue; }
            foreach (var sub in subkeys)
            {
                var candidate = ProbeUninstallSubkey(hive, uninstall, sub);
                if (candidate is not null)
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string? ProbeUninstallSubkey(Microsoft.Win32.RegistryKey hive, string uninstall, string sub)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = hive.OpenSubKey(Path.Combine(uninstall, sub));
            var name = key?.GetValue("DisplayName") as string;
            var location = key?.GetValue("InstallLocation") as string;
            if (name is not null && name.Contains("Blur", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(location))
            {
                var candidate = Path.Combine(location, "Blur.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch { }

        return null;
    }
}
