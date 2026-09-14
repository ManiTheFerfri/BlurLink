using BlurLink.Core.Session;

namespace BlurLink.Platform;

/// <summary>Adapts the named-pipe client to the coordinator's seam.</summary>
public sealed class PipeHelperChannel : IHelperChannel
{
    private readonly HelperIpcClient _client;
    public PipeHelperChannel(HelperIpcClient client) => _client = client;
    public bool IsConnected => _client.Connected;
    public Task<string> SendAsync(object message, CancellationToken ct) => _client.SendAsync(message, ct);
}
