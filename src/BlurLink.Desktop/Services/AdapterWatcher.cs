using System.Net.NetworkInformation;

namespace BlurLink.Desktop.Services;

/// <summary>
/// Raises <see cref="Changed"/> on the UI thread whenever Windows reports a
/// network address/adapter change (VPN connects, cable plugged, DHCP renew).
/// The GUI refreshes its adapter lists there — no restart needed.
/// Debounced: bursts of system events collapse into one notification.
/// </summary>
public sealed class AdapterWatcher : IDisposable
{
    private readonly SynchronizationContext? _ui;
    private readonly Timer _debounce;
    private bool _disposed;
    private int _pending;

    public event Action? Changed;

    public AdapterWatcher()
    {
        _ui = SynchronizationContext.Current;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Arm();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Arm();

    private void Arm()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _pending, 1);
        try
        {
            _debounce.Change(750, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private void Fire()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0 || _disposed)
        {
            return;
        }

        Core.Logging.AppLog.Debug("Network change detected; refreshing adapters.");
        if (_ui is not null)
        {
            _ui.Post(_ => Changed?.Invoke(), null);
        }
        else
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _debounce.Dispose();
    }
}
