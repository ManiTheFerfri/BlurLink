using BlurLink.Contracts;
using BlurLink.Core.Config;

namespace BlurLink.Core.FirstRun;

/// <summary>Writes a dated verified profile. Refuses research-mode (null port):
/// a file named "verified" must contain verified values.</summary>
public static class VerifiedProfileWriter
{
    public static string Write(GameProfile profile, string directory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.DiscoveryUdpPort is not int port || port is < 1 or > 65535)
        {
            throw new ArgumentException("A verified profile needs a verified discovery port.", nameof(profile));
        }

        Directory.CreateDirectory(directory);
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"blur-lan-verified-{date}.json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"blur-lan-verified-{date}-{n}.json");
        }

        File.WriteAllText(path, GameProfileStore.Export(profile));
        return path;
    }
}
