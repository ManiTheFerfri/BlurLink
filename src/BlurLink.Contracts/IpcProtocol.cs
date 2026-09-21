using System.Security.Cryptography;

namespace BlurLink.Contracts;

/// <summary>
/// Helpers for the authenticated local IPC channel:
/// random per-launch pipe name + unpredictable capability token.
/// </summary>
public static class IpcProtocol
{
    public static string CreatePipeName()
        => BlurLinkConstants.PipeNamePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    /// <summary>256-bit token rendered as 64 hex chars.</summary>
    public static string CreateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Constant-time token comparison to avoid leaking prefix information.
    /// Empty/null candidates never match.
    /// </summary>
    public static bool TokensEqual(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
        {
            return false;
        }

        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(actual);
        if (a.Length != b.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static bool IsValidPipeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!name.StartsWith(BlurLinkConstants.PipeNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name.Substring(BlurLinkConstants.PipeNamePrefix.Length);
        return suffix.Length == 16 && suffix.All(c => Uri.IsHexDigit(c));
    }

    public static bool IsValidTokenFormat(string? token)
        => !string.IsNullOrEmpty(token)
           && token!.Length == 64
           && token.All(c => Uri.IsHexDigit(c));
}
