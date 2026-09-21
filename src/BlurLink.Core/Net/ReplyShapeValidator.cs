namespace BlurLink.Core.Net;

/// <summary>Observe-only reply-shape rule (R6: length + leading prefix; nonce-echo
/// needs query memory the helper does not keep, so it is explicitly not here).
/// Never throws on garbage; empty expectations mean "off".</summary>
public static class ReplyShapeValidator
{
    public static bool Matches(int actualLength, byte[] actualLeading, int? expectedLength, byte[]? expectedPrefix)
    {
        if (expectedLength is null && (expectedPrefix is null || expectedPrefix.Length == 0))
        {
            return true;
        }

        if (expectedLength is int len && actualLength != len)
        {
            return false;
        }

        if (expectedPrefix is { Length: > 0 } prefix)
        {
            if (actualLeading.Length < prefix.Length)
            {
                return false;
            }

            for (var i = 0; i < prefix.Length; i++)
            {
                if (actualLeading[i] != prefix[i])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
