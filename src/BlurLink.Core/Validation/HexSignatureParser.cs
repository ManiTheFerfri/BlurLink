namespace BlurLink.Core.Validation;

/// <summary>
/// Parses an optional payload-prefix signature such as "42 4C 55 52".
/// Empty/whitespace means "no signature constraint".
/// </summary>
public static class HexSignatureParser
{
    public const int MaxSignatureBytes = 64;

    public static byte[] Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Array.Empty<byte>();
        }

        // Accept spaces, colons, dashes, commas, 0x prefixes.
        var cleaned = hex.Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(":", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);

        var tokens = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(tokens.Length);

        // Also accept a single continuous run like "424C5552".
        if (tokens.Length == 1 && tokens[0].Length > 2 && tokens[0].Length % 2 == 0 && IsHexRun(tokens[0]))
        {
            var run = tokens[0];
            for (var i = 0; i < run.Length; i += 2)
            {
                bytes.Add(Convert.ToByte(run.Substring(i, 2), 16));
            }
        }
        else
        {
            foreach (var token in tokens)
            {
                // Strict: every byte must be exactly two hex digits.
                if (token.Length != 2 || !IsHexRun(token))
                {
                    throw new FormatException($"Invalid hex byte '{token}'. Use bytes like \"42 4C 55 52\".");
                }

                bytes.Add(Convert.ToByte(token, 16));
            }
        }

        if (bytes.Count == 0)
        {
            return Array.Empty<byte>();
        }

        if (bytes.Count > MaxSignatureBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(hex), $"Signature is limited to {MaxSignatureBytes} bytes.");
        }

        return bytes.ToArray();
    }

    private static bool IsHexRun(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    public static string ToDisplayString(byte[] bytes)
        => bytes.Length == 0 ? string.Empty : string.Join(" ", bytes.Select(b => b.ToString("X2")));
}
