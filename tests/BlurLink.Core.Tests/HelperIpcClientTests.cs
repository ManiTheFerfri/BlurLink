using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BlurLink.Contracts;
using BlurLink.Platform;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Frames the GUI side of the IPC contract against an in-process pipe
/// server: newline-delimited JSON, no BOM, serialized concurrent calls.
/// (Full GUI↔helper interop is covered by scripts/test-interop.ps1.)
/// </summary>
public sealed class HelperIpcClientTests
{
    private static string PipeName() => "BlurLink-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();

    private sealed class EchoServer : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public EchoServer(string name)
        {
            _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _loop = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            await _server.WaitForConnectionAsync(_cts.Token);
            using var reader = new StreamReader(_server, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(_server, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            while (!_cts.IsCancellationRequested && _server.IsConnected)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    break;
                }

                // Echo back the "type" plus byte count so tests can verify
                // framing integrity (no BOM, no interleaving).
                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("type").GetString();
                await writer.WriteLineAsync(
                    $"{{\"type\":\"{type}\",\"bytes\":{Encoding.UTF8.GetByteCount(line)}}}");
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _server.Dispose();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_RoundTrips_WithoutBom()
    {
        var name = PipeName();
        using var server = new EchoServer(name);
        using var client = new HelperIpcClient();
        await client.ConnectAsync(name, CancellationToken.None);

        var req = new IpcSimpleCommand { Type = "get_status", Token = new string('A', 64) };
        var expectedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(req));
        var resp = await client.SendAsync(req, CancellationToken.None);

        using var doc = JsonDocument.Parse(resp);
        Assert.Equal("get_status", doc.RootElement.GetProperty("type").GetString());
        // Exact byte count => no BOM prefix was sent before the JSON line.
        Assert.Equal(expectedBytes, doc.RootElement.GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task ConcurrentSends_NeverInterleave()
    {
        var name = PipeName();
        using var server = new EchoServer(name);
        using var client = new HelperIpcClient();
        await client.ConnectAsync(name, CancellationToken.None);

        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            var req = new IpcSimpleCommand { Type = $"cmd-{i}", Token = "t" };
            var resp = await client.SendAsync(req, CancellationToken.None);
            using var doc = JsonDocument.Parse(resp);
            return doc.RootElement.GetProperty("type").GetString();
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(16, results.Distinct().Count());
        for (var i = 0; i < 16; i++)
        {
            Assert.Contains($"cmd-{i}", results);
        }
    }

    [Fact]
    public void StartRequest_Serializes_AllHelperFields()
    {
        // Locks the GUI→helper contract: the native ConfigFromStart requires
        // every one of these keys.
        var req = new IpcStartRequest
        {
            Token = "T",
            HostOverlayIp = "100.96.47.177",
            DiscoveryUdpPort = 12345,
            BroadcastDestination = "255.255.255.255",
            PayloadPrefixHex = "",
            PreserveOriginalBroadcast = true,
            RateLimitPerSecond = 10,
            RateLimitBurst = 20,
            AdapterIfIndex = 7,
        };
        var json = JsonSerializer.Serialize(req);
        using var doc = JsonDocument.Parse(json);
        foreach (var key in new[]
                 {
                     "type", "token", "hostOverlayIp", "discoveryUdpPort",
                     "broadcastDestination", "payloadPrefixHex",
                     "preserveOriginalBroadcast", "rateLimitPerSecond",
                     "rateLimitBurst", "adapterIfIndex",
                 })
        {
            Assert.True(doc.RootElement.TryGetProperty(key, out _), $"missing key: {key}");
        }
    }

    [Fact]
    public async Task ConnectAsync_FailsFast_WhenHelperDies()
    {
        using var client = new HelperIpcClient();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ConnectAsync(PipeName(), CancellationToken.None, stillAlive: () => false, timeoutSeconds: 5));
        Assert.Contains("exited", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
