using BlurLink.Contracts;
using BlurLink.Core.Session;
using BlurLink.Platform;

namespace BlurLink.Shell.ViewModels;

/// <summary>
/// Join tab facade: owns the three parts (<see cref="JoinSessionViewModel"/>,
/// <see cref="BridgeSettingsViewModel"/>, <see cref="DiscoverySniffViewModel"/>)
/// and forwards. Under 120 lines by design; any logic beyond forwarding is a defect.
/// </summary>
public sealed class JoinViewModel : ShellViewModelBase, IDisposable
{
    public JoinSessionViewModel Session { get; }
    public BridgeSettingsViewModel Settings { get; }
    public DiscoverySniffViewModel Sniff { get; }

    public JoinViewModel(
        BlurLinkConfig config,
        Action onChanged,
        IHelperProcess launcher,
        Func<Func<bool>, CancellationToken, int, Task<HelperIpcClient>> connect,
        Action dropConnection,
        Action<string> status)
    {
        JoinSessionViewModel? session = null;
        var settings = new BridgeSettingsViewModel(config, onChanged, () => session?.UpdateCoordinatorCapabilities());
        var sniff = new DiscoverySniffViewModel(
            launcher, connect, dropConnection, status, settings,
            () => session?.BridgeRunning == true,
            portText => session?.ApplySniffedPort(portText));
        session = new JoinSessionViewModel(config, onChanged, launcher, connect, dropConnection, status, settings, sniff);
        Settings = settings;
        Sniff = sniff;
        Session = session;
    }

    private JoinViewModel(JoinSessionViewModel session)
    {
        Session = session;
        Settings = session.Settings;
        Sniff = session.Sniff;
    }

    /// <summary>Test seam: drive the tab from a fake helper instead of a real one.</summary>
    public static JoinViewModel ForTests(IHelperChannel channel)
        => new(JoinSessionViewModel.ForTests(channel));

    public static SessionCapabilities ReadyForTests() => JoinSessionViewModel.ReadyForTests();

    /// <summary>One helper status through the coordinator, then rebind the view.</summary>
    public void ApplyStatusForTests(IpcStatusResponse status) => Session.ApplyStatusForTests(status);

    public SessionStory Story => Session.Story;

    public bool PreflightReady => Settings.PreflightReady;

    public void RefreshFromConfig() => Session.RefreshFromConfig();

    public void Dispose() => Session.Dispose();
}
