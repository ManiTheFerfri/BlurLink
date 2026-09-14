using System.IO;
using System.Threading;
using BlurLink.Core.Logging;

namespace BlurLink.Desktop.Services;

/// <summary>
/// Process-level single-instance guard for the GUI. Two BlurLink windows must
/// never coexist: each window can launch its own elevated helper, and two
/// helpers would both divert the same Blur discovery broadcasts (duplicate
/// forwarding, confusing counters, doubled rate limits).
///
/// Backed by a named event object rather than a named mutex:
///  - events carry no thread ownership, so "createdNew" alone is a
///    deterministic signal (no reentrancy surprises in-process);
///  - the kernel destroys the object when the last handle closes, so a
///    crashed instance leaves no stale lock behind.
///
/// The name is session-local (<c>Local\</c>): separate Windows users/sessions
/// may each run their own window without fighting over one name.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultName = @"Local\BlurLink.Desktop.SingleInstance";

    private EventWaitHandle? _handle;

    /// <summary>True when this call owns the instance slot.</summary>
    public bool IsAcquired { get; private set; }

    private SingleInstanceGuard()
    {
    }

    /// <summary>
    /// Attempts to become the single running instance. Never throws: if the
    /// platform or a transient error prevents enforcement, the guard fails
    /// open (returns acquired) so the app still starts.
    /// </summary>
    public static SingleInstanceGuard Acquire(string? name = null)
    {
        var guard = new SingleInstanceGuard();
        try
        {
            var handle = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                name ?? DefaultName,
                out bool createdNew);

            if (createdNew)
            {
                guard._handle = handle;
                guard.IsAcquired = true;
            }
            else
            {
                // Another instance holds the name: this one must not start.
                handle.Dispose();
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException
                                       or UnauthorizedAccessException
                                       or IOException
                                       or ArgumentException)
        {
            AppLog.Warn("Single-instance guard unavailable (" + ex.GetType().Name +
                        "): " + ex.Message + " — starting anyway.");
            guard.IsAcquired = true;
        }

        return guard;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
        IsAcquired = false;
    }
}
