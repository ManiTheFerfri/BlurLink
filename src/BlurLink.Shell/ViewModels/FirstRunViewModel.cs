using System.Globalization;
using BlurLink.Contracts;
using BlurLink.Core.FirstRun;

namespace BlurLink.Shell.ViewModels;

public sealed record FirstRunStep(string Title, string Detail, string ActionLabel, bool Done, string ActionView);

/// <summary>
/// Guided first run (R4): a checklist over live state, not a wizard engine.
/// Each step reads the collaborators it is given and never duplicates their
/// logic; the overlay shows once until a verified profile is written or the
/// user skips. Research mode survives as Advanced manual entry — untouched.
/// </summary>
public sealed class FirstRunViewModel : ShellViewModelBase
{
    private readonly BlurLinkConfig _config;
    private readonly BridgeSettingsViewModel _settings;
    private readonly Func<IReadOnlyList<string>> _findBlur;
    private readonly Action<string> _navigate;
    private readonly Action _onChanged;

    private IReadOnlyList<FirstRunStep> _steps = Array.Empty<FirstRunStep>();
    public IReadOnlyList<FirstRunStep> Steps
    {
        get => _steps;
        private set => Set(ref _steps, value);
    }

    private string _notice = string.Empty;
    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public string VerifiedBadge => FirstRunBadge.Text(_config);

    public RelayCommand LocateCommand { get; }
    public RelayCommand OpenJoinCommand { get; }
    public RelayCommand WriteProfileCommand { get; }
    public RelayCommand SkipCommand { get; }

    public FirstRunViewModel(
        BlurLinkConfig config,
        BridgeSettingsViewModel settings,
        Func<IReadOnlyList<string>> findBlur,
        Action<string> navigate,
        Action onChanged)
    {
        _config = config;
        _settings = settings;
        _findBlur = findBlur;
        _navigate = navigate;
        _onChanged = onChanged;

        LocateCommand = new RelayCommand(_ => LocateBlur());
        OpenJoinCommand = new RelayCommand(_ => _navigate("Join"));
        WriteProfileCommand = new RelayCommand(_ => WriteOrStart());
        SkipCommand = new RelayCommand(_ => Skip());

        _settings.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Re-reads every step from the live collaborators.</summary>
    public void Refresh()
    {
        var blurOk = File.Exists(_config.BlurExePath);
        // RouteWarning is vacuously empty until a host IP is entered (no route
        // is evaluated), so "overlay chosen" also requires the host IP: only
        // then is the route check meaningful. Fresh configs stay undone.
        var overlayOk = _settings.SelectedAdapter is not null
            && !string.IsNullOrWhiteSpace(_settings.HostIp)
            && _settings.RouteWarning == string.Empty;
        var portOk = int.TryParse(_settings.DiscoveryPort?.Trim(), out int port) && port is >= 1 and <= 65535;
        var preflightOk = _settings.PreflightReady;
        var written = !string.IsNullOrWhiteSpace(_config.VerifiedProfileDate);

        Steps = new[]
        {
            new FirstRunStep(
                "Find Blur",
                blurOk ? $"Found: {_config.BlurExePath}" : "Blur.exe not found yet — locate it, or browse in Settings.",
                "Locate…",
                blurOk,
                "Settings"),
            new FirstRunStep(
                "Choose the overlay",
                overlayOk
                    ? "Overlay adapter selected and the route check is clean."
                    : _settings.SelectedAdapter is null
                        ? "Select your overlay adapter so the route check passes."
                        : string.IsNullOrWhiteSpace(_settings.HostIp)
                            ? "Enter the host overlay IP so the route check can run."
                            : _settings.RouteWarning,
                "Open Join",
                overlayOk,
                "Join"),
            new FirstRunStep(
                "Verify the discovery port",
                portOk ? $"Discovery port {port}." : "Enter the verified discovery port (1–65535).",
                "Open Join",
                portOk,
                "Join"),
            new FirstRunStep(
                "Pre-flight",
                _settings.PreflightSummary,
                written ? "Start" : "Write verified profile",
                preflightOk,
                "Join"),
        };
        Raise(nameof(VerifiedBadge));
    }

    private void LocateBlur()
    {
        string? found = null;
        try
        {
            found = _findBlur().FirstOrDefault(f => File.Exists(f)) ?? _findBlur().FirstOrDefault();
        }
        catch
        {
            // best effort only — fall through to Settings browse
        }

        if (!string.IsNullOrWhiteSpace(found))
        {
            _config.BlurExePath = found;
            _onChanged();
            Refresh();
        }
        else
        {
            _navigate("Settings");
        }
    }

    private void WriteOrStart()
    {
        if (!string.IsNullOrWhiteSpace(_config.VerifiedProfileDate))
        {
            _navigate("Join");
            return;
        }

        if (!int.TryParse(_settings.DiscoveryPort?.Trim(), out int port) || port is < 1 or > 65535)
        {
            _navigate("Join");
            return;
        }

        try
        {
            var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var profile = new GameProfile
            {
                ProfileName = $"Blur LAN (verified {date})",
                DiscoveryUdpPort = port,
                BroadcastDestination = string.IsNullOrWhiteSpace(_settings.BroadcastDestination)
                    ? "255.255.255.255"
                    : _settings.BroadcastDestination.Trim(),
                PayloadPrefixHex = _settings.PayloadHex ?? string.Empty,
                Notes = "Written by guided first run after pre-flight.",
            };
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlurLink", "profiles");
            VerifiedProfileWriter.Write(profile, dir);
            _config.VerifiedProfileName = profile.ProfileName;
            _config.VerifiedProfileDate = date;
            _config.DiscoveryUdpPort = port;
            if (!string.IsNullOrWhiteSpace(_settings.HostIp))
            {
                _config.HostIpByProfile[profile.ProfileName] = _settings.HostIp.Trim();
            }

            Notice = string.Empty;
            _onChanged();
            Refresh();
        }
        catch (Exception ex)
        {
            Notice = "Could not write the verified profile: " + ex.Message;
        }
    }

    private void Skip()
    {
        _config.FirstRunDismissed = true;
        _onChanged();
        Refresh();
    }
}
