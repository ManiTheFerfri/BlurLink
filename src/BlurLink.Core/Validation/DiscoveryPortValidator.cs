namespace BlurLink.Core.Validation;

/// <summary>Discovery UDP destination port validation. 1-65535, no well-known Blur default.</summary>
public static class DiscoveryPortValidator
{
    public static void ValidateOrThrow(int? port, string paramName = "discoveryUdpPort")
    {
        if (port is null)
        {
            throw new ArgumentException("Discovery UDP port is unknown (Research mode). Enter the verified port from a local capture.", paramName);
        }

        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(paramName, "Port must be in range 1-65535.");
        }
    }
}
