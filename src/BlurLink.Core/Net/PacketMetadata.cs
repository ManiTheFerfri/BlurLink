namespace BlurLink.Core.Net;

/// <summary>
/// Metadata-only record for the GUI table. Never carries payload bytes.
/// </summary>
public sealed record PacketMetadata(
    DateTime TimestampUtc,
    string SrcIp,
    int SrcPort,
    string OrigDstIp,
    int OrigDstPort,
    string ForwardedDstIp,
    int Size,
    string Action);

/// <summary>
/// Redaction guard: packet metadata strings contain IPs/ports/sizes only.
/// Format() builds output only from PacketMetadata fields (the record type
/// has no payload field, so packet contents cannot leak by construction);
/// AssertNoPayloadLeak() additionally rejects anything that does not look
/// like pure metadata: long hex runs (payload dumps), the capture-format
/// raw-packet marker, and base64 blobs. Throws ArgumentException.
/// </summary>
public static class MetadataRedactor
{
    public static string Format(PacketMetadata m)
        => $"{m.TimestampUtc:HH:mm:ss.fff} {m.SrcIp}:{m.SrcPort} -> {m.OrigDstIp}:{m.OrigDstPort} " +
           $"fwd {m.ForwardedDstIp}:{m.OrigDstPort} len={m.Size} {m.Action}";

    /// <summary>
    /// Heuristic guard used by tests and any future formatting path: fails
    /// when the text looks like it carries packet bytes rather than metadata.
    /// </summary>
    public static void AssertNoPayloadLeak(string formatted)
    {
        ArgumentNullException.ThrowIfNull(formatted);

        // Long hex runs (>16 chars, i.e. >8 payload bytes in one blob) are
        // how payload dumps show up. Metadata never contains such a run:
        // IPv4 addresses max out at 12 digits separated by dots, ports at
        // 5 digits, sizes at 10.
        if (ContainsLongHexRun(formatted, minRun: 17))
        {
            throw new ArgumentException(
                "Formatted metadata contains a long hex run (possible payload leak).", nameof(formatted));
        }

        // The classic capture-dump prefix, should a future path ever embed
        // a raw capture line.
        if (formatted.Contains("0x0000", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Formatted metadata contains a raw capture marker (possible payload leak).", nameof(formatted));
        }

        // Base64 blobs (>= 24 chars from the base64 alphabet incl. padding).
        // IPs/ports/actions can never satisfy the padding+length shape.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                formatted, @"[A-Za-z0-9+/]{24,}={1,2}"))
        {
            throw new ArgumentException(
                "Formatted metadata contains a base64 blob (possible payload leak).", nameof(formatted));
        }

        // Spaced hex byte dumps (8+ consecutive two-digit hex bytes, the
        // classic "42 4C 55 52 ..." payload listing). Metadata tokens are
        // never two-hex-digit pairs separated by single spaces.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                formatted, @"(?:[0-9A-Fa-f]{2} ){7,}[0-9A-Fa-f]{2}"))
        {
            throw new ArgumentException(
                "Formatted metadata contains a hex byte dump (possible payload leak).", nameof(formatted));
        }
    }

    private static bool ContainsLongHexRun(string text, int minRun)
    {
        int run = 0;
        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                run++;
                if (run >= minRun)
                {
                    return true;
                }
            }
            else
            {
                run = 0;
            }
        }

        return false;
    }
}
