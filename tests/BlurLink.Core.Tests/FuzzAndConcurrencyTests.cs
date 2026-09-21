using System.Buffers.Binary;
using System.Text;
using BlurLink.Core.Logging;
using BlurLink.Core.Net;
using BlurLink.Core.Validation;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Deterministic fuzz (fixed seed): parsers must never throw unexpected
/// exceptions, never over-match, and the packet transform must preserve its
/// invariants on every input it accepts.
/// </summary>
public sealed class FuzzTests
{
    private const int Seed = 0xB10C;
    private const int Iterations = 5000;

    private static Random Rng() => new(Seed);

    private static string RandomChunk(Random rng, string alphabet, int maxLen)
    {
        var len = rng.Next(maxLen + 1);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }

        return sb.ToString();
    }

    [Fact]
    public void Ipv4Validator_NeverThrows_AndAgreesWithCanonicalForm()
    {
        var rng = Rng();
        const string alpha = "0123456789.:abcdefABCDEF \t\nxyz/\\-";
        for (var i = 0; i < Iterations; i++)
        {
            var s = RandomChunk(rng, alpha, 24);
            bool ok;
            try
            {
                ok = Ipv4Validator.TryParse(s, out var addr);
                if (ok)
                {
                    // Canonical round-trip: re-serializing must give the trimmed input.
                    Assert.NotNull(addr);
                    Assert.Equal(s.Trim(), addr!.ToString());
                }
            }
            catch (Exception ex)
            {
                Assert.Fail($"TryParse threw {ex.GetType().Name} on '{s}'");
                return;
            }
        }
    }

    [Fact]
    public void HexParser_OnlyThrowsDocumentedExceptions()
    {
        var rng = Rng();
        const string alpha = "0123456789abcdefABCDEF :-,xX\t ";
        for (var i = 0; i < Iterations; i++)
        {
            var s = RandomChunk(rng, alpha, 40);
            try
            {
                var bytes = HexSignatureParser.Parse(s);
                Assert.True(bytes.Length <= HexSignatureParser.MaxSignatureBytes);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
            {
                // documented rejections only
            }
        }
    }

    [Fact]
    public void PacketParser_NeverThrows_AndOffsetsStaySane()
    {
        var rng = Rng();
        var buf = new byte[128];
        for (var i = 0; i < Iterations; i++)
        {
            rng.NextBytes(buf);
            var len = rng.Next(buf.Length + 1);
            bool ok = PacketTransformer.TryParseUdpOverIpv4(
                buf.AsSpan(0, len), out _, out _, out int ihl, out int off, out int payLen,
                out string? reason);
            if (ok)
            {
                Assert.True(ihl is >= 20 and <= 60);
                Assert.True(off >= ihl + 8);
                Assert.True(off + payLen <= len);
                Assert.Null(reason);
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(reason));
            }
        }
    }

    [Fact]
    public void TransformFuzz_InvariantsHoldOnEveryAcceptedPacket()
    {
        var rng = Rng();
        var buf = new byte[256];
        var accepted = 0;
        for (var i = 0; i < Iterations; i++)
        {
            rng.NextBytes(buf);
            var len = 28 + rng.Next(200);
            if (i % 2 == 0)
            {
                // Structured: force coherent length fields so most inputs parse.
                buf[0] = 0x45;
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)len);
                buf[6] = 0x00;
                buf[7] = 0x00;
                buf[9] = 17;
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(24, 2), (ushort)(len - 20));
            }

            if (!PacketTransformer.TryParseUdpOverIpv4(buf.AsSpan(0, len),
                    out _, out _, out _, out _, out _, out _))
            {
                continue;
            }

            var orig = buf[..len];
            var before = (byte[])orig.Clone();
            var clone = PacketTransformer.CloneWithNewDestination(orig, new byte[] { 10, 9, 9, 9 });
            accepted++;
            Assert.Equal(before, orig); // input untouched
            Assert.Equal(orig.Length, clone.Length);
            Assert.Equal(orig[12..16], clone[12..16]); // src same
            Assert.Equal(new byte[] { 10, 9, 9, 9 }, clone[16..20]); // dst replaced
            Assert.Equal(orig[20..24], clone[20..24]); // ports same
            Assert.Equal(orig[28..], clone[28..]); // payload same
            Assert.True(PacketTransformer.VerifyIpChecksum(clone));
        }

        Assert.True(accepted > 100, "fuzz should accept a healthy share of biased packets");
    }

    [Fact]
    public void UdpLengthField_Fuzz_ParserStaysConsistent()
    {
        // Randomize only the UDP length field of an otherwise valid packet.
        var rng = Rng();
        var good = new byte[30];
        good[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(good.AsSpan(2, 2), 30);
        good[8] = 64;
        good[9] = 17;
        var valid = 0;
        for (var i = 0; i < 2000; i++)
        {
            var pkt = (byte[])good.Clone();
            var udpLen = rng.Next(48);
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(24, 2), (ushort)udpLen);
            bool ok = PacketTransformer.TryParseUdpOverIpv4(pkt, out _, out _, out _, out _, out _, out _);
            // Valid only when 8 <= udpLen and 20 + udpLen <= 30.
            Assert.Equal(udpLen is >= 8 and <= 10, ok);
            if (ok)
            {
                valid++;
            }
        }

        Assert.True(valid > 0);
    }
}

public sealed class ConcurrencyTests
{
    [Fact]
    public void RateLimiter_ParallelTotals_AddUp()
    {
        var limiter = new RateLimiter(perSecond: 100, burst: 64);
        var acquired = 0;
        var threads = 8;
        var perThread = 200;
        Parallel.For(0, threads, _ =>
        {
            var local = 0;
            for (var i = 0; i < perThread; i++)
            {
                if (limiter.TryAcquire())
                {
                    local++;
                }
            }

            Interlocked.Add(ref acquired, local);
        });

        var total = threads * perThread;
        Assert.Equal(total, acquired + limiter.RejectedCount);
        Assert.True(acquired >= 64, "burst must always be grantable");
        Assert.True(acquired <= 64 + 100 * 10, "refill must stay bounded");
    }

    [Fact]
    public void PacketMetadataFormatting_IsThreadSafe()
    {
        var m = new Net.PacketMetadata(
            DateTime.UtcNow, "100.96.21.89", 50000,
            "255.255.255.255", 12345, "100.96.47.177", 64, "forwarded");
        var failures = 0;
        Parallel.For(0, 1000, _ =>
        {
            try
            {
                var s = Net.MetadataRedactor.Format(m);
                if (!s.Contains("forwarded"))
                {
                    Interlocked.Increment(ref failures);
                }
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
        });
        Assert.Equal(0, failures);
    }
}

public sealed class AppLogTests
{
    [Fact]
    public void Log_WritesFile_RespectsLevel_AndNeverThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(dir, "test.log");
        AppLog.Initialize("Warning", path);
        try
        {
            AppLog.Info("info-suppressed");
            AppLog.Warn("warn-kept");
            AppLog.Error("error-kept");
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("info-suppressed", text);
            Assert.Contains("warn-kept", text);
            Assert.Contains("error-kept", text);
        }
        finally
        {
            AppLog.Initialize("Information"); // restore default path/level
        }
    }

    [Fact]
    public void Log_BeforeInitialize_IsSilent()
    {
        // Must never throw even with a bogus path configured afterward.
        AppLog.Initialize("Information", Path.Combine("\\\\?\\invalid", "x.log"));
        AppLog.Error("goes nowhere, throws nothing");
        AppLog.Initialize("Information");
    }
}
