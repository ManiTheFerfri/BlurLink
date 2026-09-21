using BlurLink.Core.Validation;

namespace BlurLink.Core.Verify;

public sealed record PrefixStabilityResult(bool Agreed, string PrefixHex, string Reason);

/// <summary>Three-capture stability rule (spec Q3, settled at 12 bytes): the first
/// 12 payload bytes must be identical across at least three parseable samples
/// before they may become a profile prefix. Pasted from the user's own Wireshark —
/// the sniffer is metadata-only and never yields payload bytes (R5).</summary>
public static class PrefixStability
{
    public const int RequiredSamples = 3;
    public const int PrefixLength = 12;

    public static PrefixStabilityResult Check(IReadOnlyList<string> samples)
    {
        var parsed = new List<byte[]>();
        foreach (var sample in samples)
        {
            try
            {
                var bytes = HexSignatureParser.Parse(sample);
                if (bytes.Length >= PrefixLength)
                {
                    parsed.Add(bytes);
                }
            }
            catch
            {
                // garbage is ignored, never fatal — it just does not count
            }
        }

        if (parsed.Count < RequiredSamples)
        {
            return new PrefixStabilityResult(false, string.Empty,
                $"Need {RequiredSamples} parseable samples of {PrefixLength}+ bytes; got {parsed.Count}.");
        }

        var first = parsed[0].Take(PrefixLength).ToArray();
        for (var i = 1; i < parsed.Count; i++)
        {
            if (!parsed[i].Take(PrefixLength).SequenceEqual(first))
            {
                return new PrefixStabilityResult(false, string.Empty,
                    $"Sample {i + 1} differs inside the first {PrefixLength} bytes — leave the signature empty.");
            }
        }

        return new PrefixStabilityResult(true,
            string.Join(" ", first.Select(b => b.ToString("X2"))),
            "Stable across captures.");
    }
}
