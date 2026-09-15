using System.Globalization;
using BlurLink.Contracts;
using BlurLink.Core.FirstRun;
using BlurLink.Core.Verify;

namespace BlurLink.Shell.ViewModels;

/// <summary>Verify-profile tool (the Diagnostics Verify section): checks three
/// pasted captures for a stable 12-byte prefix, then writes a dated verified
/// profile through Task 8's writer. The discovery port and broadcast come from
/// the live config (Join keeps them current); the stable prefix plus all three
/// flow back into Join through the apply callbacks so the tab shows the
/// verified values. Host memory follows R8 (this VM only touches
/// <c>HostIpByProfile</c>).</summary>
public sealed class VerifyProfileViewModel : ShellViewModelBase
{
    private readonly BlurLinkConfig _config;
    private readonly Action<string> _applyPort;
    private readonly Action<string> _applyBroadcast;
    private readonly Action<string> _applyPrefix;
    private readonly Action _onChanged;

    private bool _agreed;
    private string _stablePrefix = string.Empty;

    private string _sample1 = string.Empty;
    public string Sample1 { get => _sample1; set => Set(ref _sample1, value); }

    private string _sample2 = string.Empty;
    public string Sample2 { get => _sample2; set => Set(ref _sample2, value); }

    private string _sample3 = string.Empty;
    public string Sample3 { get => _sample3; set => Set(ref _sample3, value); }

    private string _profileName = string.Empty;
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }

    private string _result = string.Empty;
    public string Result { get => _result; private set => Set(ref _result, value); }

    public RelayCommand CheckCommand { get; }
    public RelayCommand WriteCommand { get; }

    public VerifyProfileViewModel(
        BlurLinkConfig config,
        Action<string> applyPort,
        Action<string> applyBroadcast,
        Action<string> applyPrefix,
        Action onChanged)
    {
        _config = config;
        _applyPort = applyPort;
        _applyBroadcast = applyBroadcast;
        _applyPrefix = applyPrefix;
        _onChanged = onChanged;

        CheckCommand = new RelayCommand(_ => Check());
        WriteCommand = new RelayCommand(_ => Write(), _ => _agreed);
    }

    private void Check()
    {
        var check = PrefixStability.Check(new[] { Sample1, Sample2, Sample3 });
        _agreed = check.Agreed;
        _stablePrefix = check.PrefixHex;
        if (check.Agreed)
        {
            if (string.IsNullOrWhiteSpace(ProfileName))
            {
                ProfileName = $"Blur LAN (verified {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})";
            }

            Result = $"Stable prefix ({PrefixStability.PrefixLength} bytes): {check.PrefixHex}";
        }
        else
        {
            Result = check.Reason;
        }

        WriteCommand.RaiseCanExecuteChanged();
    }

    private void Write()
    {
        if (!_agreed || string.IsNullOrWhiteSpace(_stablePrefix))
        {
            Result = "Check prefix stability first — nothing stable to write yet.";
            return;
        }

        if (_config.DiscoveryUdpPort is not int port || port is < 1 or > 65535)
        {
            Result = "Set the verified discovery port on the Join tab first, then write the profile.";
            return;
        }

        var broadcast = string.IsNullOrWhiteSpace(_config.BroadcastDestination)
            ? "255.255.255.255"
            : _config.BroadcastDestination.Trim();
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = string.IsNullOrWhiteSpace(ProfileName)
            ? $"Blur LAN (verified {date})"
            : ProfileName.Trim();
        var profile = new GameProfile
        {
            ProfileName = name,
            DiscoveryUdpPort = port,
            BroadcastDestination = broadcast,
            PayloadPrefixHex = _stablePrefix,
            Notes = "Written by the Diagnostics verify tool after a three-capture stability check.",
        };

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlurLink", "profiles");
            var path = VerifiedProfileWriter.Write(profile, dir);
            _config.VerifiedProfileName = name;
            _config.VerifiedProfileDate = date;
            _config.DiscoveryUdpPort = port;
            _config.BroadcastDestination = broadcast;
            _config.PayloadPrefixHex = _stablePrefix;
            if (!string.IsNullOrWhiteSpace(_config.HostOverlayIp))
            {
                _config.HostIpByProfile[name] = _config.HostOverlayIp.Trim();
            }

            _applyPort(port.ToString(CultureInfo.InvariantCulture));
            _applyBroadcast(broadcast);
            _applyPrefix(_stablePrefix);
            _onChanged();
            Result = $"Wrote {name} → {path}";
        }
        catch (Exception ex)
        {
            Result = "Could not write the verified profile: " + ex.Message;
        }
    }
}
