using System.Globalization;
using BlurLink.Contracts;
using BlurLink.Core.FirstRun;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class FirstRunTests
{
    [Fact]
    public void Writer_NamesTheFile_ByDate_AndAvoidsCollisions()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var profile = new GameProfile
        {
            ProfileName = $"Blur LAN (verified {date})",
            DiscoveryUdpPort = 50001,
            BroadcastDestination = "255.255.255.255",
            PayloadPrefixHex = "0F 00 00",
            Notes = "unit fixture",
        };

        var first = VerifiedProfileWriter.Write(profile, dir);
        var second = VerifiedProfileWriter.Write(profile, dir);

        Assert.Equal(Path.Combine(dir, $"blur-lan-verified-{date}.json"), first);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.Contains($"verified {date}", File.ReadAllText(first));
    }

    [Fact]
    public void Writer_Refuses_AnUnverifiedProfile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        Assert.Throws<ArgumentException>(() =>
            VerifiedProfileWriter.Write(GameProfile.ResearchMode(), dir));
    }

    [Fact]
    public void Finder_AlwaysReturns_AList_NeverThrows()
    {
        var candidates = BlurFinder.Candidates();

        Assert.NotNull(candidates);
    }

    [Fact]
    public void Badge_ShowsResearchMode_UntilAProfileIsWritten()
    {
        var config = BlurLinkConfig.CreateDefault();

        Assert.Equal("Research mode", FirstRunBadge.Text(config));

        config.VerifiedProfileName = "Blur LAN (verified 2026-09-14)";
        config.VerifiedProfileDate = "2026-09-14";

        Assert.Equal("Verified 2026-09-14", FirstRunBadge.Text(config));
    }
}
