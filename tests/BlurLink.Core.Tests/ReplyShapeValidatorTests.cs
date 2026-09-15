using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// The observe-only reply-shape rule (R6: length + leading prefix).
/// Fixtures are synthetic payloads at real lengths only — never real capture
/// bytes (privacy rule). Scrubbed 10.0.0.x addresses are fine.
/// </summary>
public sealed class ReplyShapeValidatorTests
{
    /// <summary>Synthetic 160-byte reply (the real reply length) carrying a
    /// scrubbed 10.0.0.20 address at a documented offset. Every byte is made
    /// up; only the length and the scrubbed address shape are real.</summary>
    private static byte[] Synthetic160ByteReply()
    {
        var payload = new byte[160];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(0xA0 + (i & 0x0F));
        }

        // Scrubbed endpoint address at offset 32, like the real layout.
        payload[32] = 10;
        payload[33] = 0;
        payload[34] = 0;
        payload[35] = 20;
        return payload;
    }

    [Fact]
    public void NoExpectations_MatchesAnything()
    {
        var reply = Synthetic160ByteReply();

        Assert.True(ReplyShapeValidator.Matches(reply.Length, reply, null, null));
        Assert.True(ReplyShapeValidator.Matches(reply.Length, reply, null, Array.Empty<byte>()));
    }

    [Fact]
    public void LengthMismatch_DoesNotMatch()
    {
        var reply = Synthetic160ByteReply();

        Assert.False(ReplyShapeValidator.Matches(reply.Length, reply, 24, null));
    }

    [Fact]
    public void PrefixMismatch_DoesNotMatch()
    {
        var reply = Synthetic160ByteReply();
        var expected = reply.Take(12).ToArray();
        expected[5] ^= 0xFF; // flip one byte: otherwise identical at real length

        Assert.False(ReplyShapeValidator.Matches(reply.Length, reply, 160, expected));
    }

    [Fact]
    public void ShortBuffer_DoesNotMatch()
    {
        var reply = Synthetic160ByteReply();
        var expected = reply.Take(12).ToArray();

        Assert.False(ReplyShapeValidator.Matches(reply.Length, reply.Take(4).ToArray(), 160, expected));
    }
}
