using BlurLink.Contracts;
using BlurLink.Core.Config;
using BlurLink.Desktop.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Regression: Settings Import/Reset wrote the shared config but the Join tab
/// kept its old values, so the next Start silently overwrote the imported
/// values back to the stale ones. Both directions must now agree.
/// </summary>
public sealed class SettingsImportResetTests
{
    private static (JoinViewModel Join, SettingsViewModel Settings) Build(
        BlurLinkConfig config, Action<string>? applyLogLevel = null)
    {
        var join = new JoinViewModel(
            config,
            () => { },
            new FakeHelperProcess("BlurLink-0123456789ABCDEF", new string('A', 64)),
            (_, _, _) => throw new InvalidOperationException("helper must not launch in this test"),
            () => { },
            _ => { });
        var settings = new SettingsViewModel(config, join.RefreshFromConfig, applyLogLevel);
        return (join, settings);
    }

    [Fact]
    public void Import_RefreshesJoinFields_SoStartCannotClobberImportedValues()
    {
        var config = BlurLinkConfig.CreateDefault();
        var (join, settings) = Build(config);
        try
        {
            join.HostIp = "10.0.0.1";
            join.DiscoveryPort = "1234";

            settings.ExportText = BlurLinkConfigStore.ExportJson(new BlurLinkConfig
            {
                HostOverlayIp = "100.96.47.177",
                DiscoveryUdpPort = 50001,
                BroadcastDestination = "255.255.255.255",
                PayloadPrefixHex = "42 4C",
            });
            settings.ImportCommand.Execute(null);

            Assert.Equal("100.96.47.177", join.HostIp);
            Assert.Equal("50001", join.DiscoveryPort);
            Assert.Equal("42 4C", join.PayloadHex);
            // Config and Join tab now agree, so Start persists the imported values.
            Assert.Equal("100.96.47.177", config.HostOverlayIp);
            Assert.Equal(50001, config.DiscoveryUdpPort);
        }
        finally
        {
            join.Dispose();
            settings.Dispose();
        }
    }

    [Fact]
    public void Import_PushesTheImportedLogLevelIntoTheLiveLogger()
    {
        var config = BlurLinkConfig.CreateDefault();
        var applied = new List<string>();
        var (join, settings) = Build(config, applied.Add);
        try
        {
            settings.ExportText = BlurLinkConfigStore.ExportJson(
                new BlurLinkConfig { LogLevel = "Error" });
            settings.ImportCommand.Execute(null);

            // The imported level must reach the logger, not just settings.json:
            // otherwise the file says Error while Debug chatter keeps flowing.
            Assert.Equal("Error", config.LogLevel);
            Assert.Equal(new[] { "Error" }, applied);
        }
        finally
        {
            join.Dispose();
            settings.Dispose();
        }
    }

    [Fact]
    public void Reset_PushesTheDefaultLogLevelIntoTheLiveLogger()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.LogLevel = "Debug";
        var applied = new List<string>();
        var (join, settings) = Build(config, applied.Add);
        try
        {
            settings.ResetCommand.Execute(null);

            Assert.Equal(BlurLinkConstants.DefaultLogLevel, config.LogLevel);
            Assert.Equal(new[] { BlurLinkConstants.DefaultLogLevel }, applied);
        }
        finally
        {
            join.Dispose();
            settings.Dispose();
        }
    }

    [Fact]
    public void Save_StillPushesTheChosenLogLevelIntoTheLiveLogger()
    {
        var config = BlurLinkConfig.CreateDefault();
        var applied = new List<string>();
        var (join, settings) = Build(config, applied.Add);
        try
        {
            settings.LogLevel = "Warning";
            settings.SaveCommand.Execute(null);

            Assert.Equal("Warning", config.LogLevel);
            Assert.Equal(new[] { "Warning" }, applied);
        }
        finally
        {
            join.Dispose();
            settings.Dispose();
        }
    }

    [Fact]
    public void Reset_ClearsResearchValuesInConfigAndOnTheJoinTab()
    {
        var config = BlurLinkConfig.CreateDefault();
        config.HostOverlayIp = "100.96.47.177";
        config.DiscoveryUdpPort = 50001;
        config.PayloadPrefixHex = "42 4C";
        var (join, settings) = Build(config);
        try
        {
            Assert.Equal("100.96.47.177", join.HostIp);

            settings.ResetCommand.Execute(null);

            Assert.Equal(string.Empty, join.HostIp);
            Assert.Equal(string.Empty, join.DiscoveryPort);
            Assert.Equal(string.Empty, join.PayloadHex);
            Assert.Equal(string.Empty, config.HostOverlayIp);
            Assert.Null(config.DiscoveryUdpPort);
            Assert.Equal(string.Empty, config.PayloadPrefixHex);
        }
        finally
        {
            join.Dispose();
            settings.Dispose();
        }
    }
}
