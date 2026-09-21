using BlurLink.Contracts;
using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class IpcAuthTests
{
    [Fact]
    public void PipeName_IsRandomPerLaunch_AndValid()
    {
        var a = IpcProtocol.CreatePipeName();
        var b = IpcProtocol.CreatePipeName();
        Assert.NotEqual(a, b);
        Assert.True(IpcProtocol.IsValidPipeName(a));
        Assert.True(IpcProtocol.IsValidPipeName(b));
        Assert.False(IpcProtocol.IsValidPipeName("BlurLink-fixed"));
        Assert.False(IpcProtocol.IsValidPipeName("\\\\.\\pipe\\other"));
    }

    [Fact]
    public void Token_IsUnpredictable_AndWellFormed()
    {
        var a = IpcProtocol.CreateToken();
        var b = IpcProtocol.CreateToken();
        Assert.NotEqual(a, b);
        Assert.True(IpcProtocol.IsValidTokenFormat(a));
        Assert.False(IpcProtocol.IsValidTokenFormat("short"));
        Assert.False(IpcProtocol.IsValidTokenFormat(null));
    }

    [Fact]
    public void TokenComparison_ExactMatchOnly()
    {
        var t = IpcProtocol.CreateToken();
        Assert.True(IpcProtocol.TokensEqual(t, t));
        // Flip the last nibble: must no longer match.
        var tampered = t[..63] + (t[63] == '0' ? '1' : '0');
        Assert.False(IpcProtocol.TokensEqual(t, tampered));
        Assert.False(IpcProtocol.TokensEqual(t, null));
        Assert.False(IpcProtocol.TokensEqual(null, t));
        Assert.False(IpcProtocol.TokensEqual(t, t + "00"));
        Assert.False(IpcProtocol.TokensEqual(string.Empty, t));
    }
}

public sealed class PacketMetadataRedactionTests
{
    [Fact]
    public void Metadata_ContainsNoPayload()
    {
        // Pinned clock on purpose: a timestamp like 18:42:07.420 used to trip
        // this guard (~1 run in 12) when the wall clock contained "42", so the
        // check must not depend on when the suite happens to run.
        var m = new PacketMetadata(
            new DateTime(2026, 9, 11, 18, 42, 7, 420, DateTimeKind.Utc), "100.96.21.89", 50000,
            "255.255.255.255", 12345, "100.96.47.177", 64, "forwarded");

        var formatted = MetadataRedactor.Format(m);
        Assert.Contains("18:42:07.420", formatted);
        Assert.Contains("100.96.21.89:50000", formatted);
        Assert.Contains("forwarded", formatted);
        // Payload bytes must never appear: the record type has no payload field.
        // Drop the leading clock token first: those digits are the wall clock,
        // not metadata, and a run at 18:42:07 used to fail this guard (~1 in 12
        // runs) for a payload that was never there.
        var fieldsOnly = string.Join(' ', formatted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            .Replace("50000", string.Empty)
            .Replace("12345", string.Empty);
        Assert.DoesNotContain("42", fieldsOnly);
    }
}

public sealed class LoopPreventionTests
{    [Fact]
    public void CloneDestination_IsUnicast_SoItCannotRematchBroadcastFilter()
    {
        // The bridge filter matches ONLY the broadcast destination; the clone
        // goes to a unicast host IP, which the filter builder always rejects
        // as a destination. Structural loop immunity.
        Assert.Throws<ArgumentException>(() => WinDivertFilterBuilder.Build(
            new WinDivertFilterBuilder.FilterInput(5000, "100.96.47.177")));
    }

    [Fact]
    public void OriginalBroadcast_PreservedByDefault_InConfig()
    {
        Assert.True(BlurLinkConfig.CreateDefault().PreserveOriginalBroadcast);
    }

    [Fact]
    public void Profile_ResearchModeKeepsNameButDefaultsToFixedPort()
    {
        // Task A: the name stays for compat, but the null port retired —
        // ResearchMode() now carries the verified fixed port.
        var p = GameProfile.ResearchMode();
        Assert.Equal("Research mode", p.ProfileName);
        Assert.Equal(BlurLinkConstants.DiscoveryUdpPortDefault, p.DiscoveryUdpPort);
        Assert.Equal(50001, p.DiscoveryUdpPort);
    }
}

public sealed class ReplySummaryTests
{
    [Fact]
    public void EmptyResults_AsksForRefresh()
    {
        var s = BlurLink.Desktop.ViewModels.JoinViewModel.FormatReplySummary(
            Array.Empty<Contracts.SniffPortCount>(), "10.88.132.54");
        Assert.Contains("No replies yet", s);
    }

    [Fact]
    public void HostAnswer_RecognizedByIp()
    {
        var results = new List<Contracts.SniffPortCount>
        {
            new() { Port = 50001, Count = 3, SrcIp = "10.88.132.54" },
            new() { Port = 50001, Count = 1, SrcIp = "10.9.9.9" },
        };
        var s = BlurLink.Desktop.ViewModels.JoinViewModel.FormatReplySummary(results, "10.88.132.54");
        Assert.Contains("Host answered", s);
        Assert.Contains("10.88.132.54", s);
    }

    [Fact]
    public void ForeignAnswer_Flagged()
    {
        var results = new List<Contracts.SniffPortCount>
        {
            new() { Port = 50001, Count = 2, SrcIp = "10.9.9.9" },
        };
        var s = BlurLink.Desktop.ViewModels.JoinViewModel.FormatReplySummary(results, "10.88.132.54");
        Assert.Contains("not the host", s);
    }
}
