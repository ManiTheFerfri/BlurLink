using BlurLink.Contracts;
using BlurLink.Platform;
using BlurLink.Desktop.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Regression tests for the JoinViewModel busy-state machine.
/// Previously, StartAsync set _busy = true and returned early on validation
/// or filter-build failure BEFORE the try/finally that resets it, which
/// permanently disabled Start/Stop/Detect until the app was restarted.
/// </summary>
public sealed class JoinViewModelBusyStateTests : IDisposable
{
    private readonly MainViewModel _vm;

    public JoinViewModelBusyStateTests()
    {
        _vm = new MainViewModel();
    }

    public void Dispose()
    {
        _vm.Join.Dispose();
        _vm.Dispose();
    }

    [Fact]
    public async Task StartAsync_WithInvalidInput_ReleasesBusy()
    {
        // No host IP / port entered: validation must fail.
        _vm.Join.HostIp = "not-an-ip";
        await _vm.Join.StartAsync();
        Assert.False(_vm.Join.BridgeRunning);
        Assert.False(string.IsNullOrEmpty(_vm.Join.Message));
        // The commands must remain enabled (busy flag released).
        Assert.True(_vm.Join.StartStopCommand.CanExecute(null));
        Assert.True(_vm.Join.DetectCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartAsync_WithInvalidPort_ReleasesBusy()
    {
        _vm.Join.HostIp = "100.96.47.177";
        _vm.Join.DiscoveryPort = "70000"; // out of range
        await _vm.Join.StartAsync();
        Assert.False(_vm.Join.BridgeRunning);
        Assert.True(_vm.Join.StartStopCommand.CanExecute(null));
        Assert.True(_vm.Join.ListenRepliesCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartAsync_TwiceAfterFailure_StillValidates()
    {
        _vm.Join.HostIp = "not-an-ip";
        await _vm.Join.StartAsync();
        _vm.Join.HostIp = "also-bad";
        await _vm.Join.StartAsync();
        // Still responsive: the busy flag must not be latched.
        Assert.True(_vm.Join.StartStopCommand.CanExecute(null));
        Assert.False(_vm.Join.BridgeRunning);
    }

    [Fact]
    public async Task SniffAsync_RepliesWithoutPort_ReleasesBusyAndNeverLaunchesHelper()
    {
        // Force the missing-port validation path so no helper is ever
        // launched (tests must stay hermetic; this machine may even have
        // the WinDivert driver installed).
        _vm.Join.DiscoveryPort = string.Empty;
        await _vm.Join.SniffAsync(replies: true);
        Assert.Contains("Set the discovery port first", _vm.Join.SniffStatus);
        Assert.False(_vm.Join.SniffRunning);
        Assert.True(_vm.Join.DetectCommand.CanExecute(null));
    }

    [Fact]
    public void StartStopText_ReflectsState()
    {
        Assert.Equal("Start Bridge", _vm.Join.StartStopText);
        Assert.True(_vm.Join.ShowStart);
        Assert.False(_vm.Join.ShowForceKill);
    }
}
