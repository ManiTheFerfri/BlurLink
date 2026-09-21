using System.Text.Json;
using BlurLink.Contracts;

namespace BlurLink.Core.Config;

/// <summary>
/// Loads/saves %LocalAppData%\BlurLink\settings.json with forward-compatible
/// migration. Never persists payloads or credentials (schema has none).
/// </summary>
public sealed class BlurLinkConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlurLink", "settings.json");

    public BlurLinkConfig Load(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            if (!File.Exists(path))
            {
                return BlurLinkConfig.CreateDefault();
            }

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<BlurLinkConfig>(json, JsonOptions);
            return Migrate(cfg);
        }
        catch
        {
            // Corrupt file: keep defaults rather than crash, but preserve the
            // bytes as <path>.corrupt so research values stay recoverable.
            try
            {
                var backup = path + ".corrupt";
                File.Copy(path, backup, overwrite: true);
                Core.Logging.AppLog.Warn(
                    "settings.json was unreadable; backed up to " + backup +
                    ". Defaults loaded — restore verified values manually.");
            }
            catch
            {
                // best effort: never fail loading over the backup
            }

            return BlurLinkConfig.CreateDefault();
        }
    }

    public void Save(BlurLinkConfig config, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        path ??= DefaultPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Clamp rate limit into the safe range on save.
        config.RateLimitPerSecond = Math.Clamp(
            config.RateLimitPerSecond, 1, BlurLinkConstants.MaxRateLimitPerSecond);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        // Write atomically: temp + move.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Migration/defaulting: fills missing fields from older versions.</summary>
    public static BlurLinkConfig Migrate(BlurLinkConfig? cfg)
    {
        cfg ??= new BlurLinkConfig();
        cfg.LastMode = cfg.LastMode is "host" or "join" ? cfg.LastMode : "join";
        cfg.BlurArgs ??= string.Empty;
        cfg.BroadcastDestination = string.IsNullOrWhiteSpace(cfg.BroadcastDestination)
            ? "255.255.255.255" : cfg.BroadcastDestination.Trim();
        cfg.PayloadPrefixHex ??= string.Empty;
        cfg.HostOverlayIp ??= string.Empty;
        cfg.BlurExePath ??= string.Empty;
        if (cfg.DiscoveryUdpPort is null or 0) cfg.DiscoveryUdpPort = BlurLinkConstants.DiscoveryUdpPortDefault;
        cfg.VerifiedProfileName ??= string.Empty; cfg.VerifiedProfileDate ??= string.Empty; cfg.HostIpByProfile ??= new();
        // Unknown/empty levels map to the default, so an imported or hand-edited
        // settings.json can never leave the logger and the UI disagreeing.
        cfg.LogLevel = BlurLinkConstants.NormalizeLogLevel(cfg.LogLevel);
        cfg.RateLimitPerSecond = Math.Clamp(
            cfg.RateLimitPerSecond == 0 ? BlurLinkConstants.DefaultRateLimitPerSecond : cfg.RateLimitPerSecond,
            1, BlurLinkConstants.MaxRateLimitPerSecond);
        return cfg;
    }

    public static string ExportJson(BlurLinkConfig config)
        => JsonSerializer.Serialize(Migrate(config), JsonOptions);

    public static BlurLinkConfig ImportJson(string json)
    {
        var cfg = JsonSerializer.Deserialize<BlurLinkConfig>(json, JsonOptions);
        return Migrate(cfg);
    }
}
