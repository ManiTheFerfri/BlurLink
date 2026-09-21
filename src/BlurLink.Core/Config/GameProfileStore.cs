using System.Text.Json;
using BlurLink.Contracts;

namespace BlurLink.Core.Config;

/// <summary>Import/export of discovery profiles (JSON, no payloads).</summary>
public static class GameProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Export(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(profile, Options);
    }

    public static GameProfile Import(string json)
    {
        var p = JsonSerializer.Deserialize<GameProfile>(json, Options)
            ?? throw new InvalidDataException("Invalid profile JSON.");
        if (string.IsNullOrWhiteSpace(p.ProfileName))
        {
            p.ProfileName = "Research mode";
        }

        return p;
    }
}
