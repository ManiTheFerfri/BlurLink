namespace BlurLink.Core.Session;

/// <summary>
/// The one thing the coordinator needs from a helper: send a message, get the
/// JSON reply. Implemented in the app over the named pipe, faked in tests.
/// </summary>
public interface IHelperChannel
{
    bool IsConnected { get; }
    Task<string> SendAsync(object message, CancellationToken ct);
}
