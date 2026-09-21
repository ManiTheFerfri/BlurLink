using System.IO.Pipes;
using System.Text;
using BlurLink.Platform;

namespace BlurLink.Core.Tests;

/// <summary>
/// In-memory stand-in for the elevated helper process. Records launch/kill
/// decisions so tests can assert the GUI never tears down a live session
/// (which would silently stop the bridge).
/// </summary>
internal sealed class FakeHelperProcess : IHelperProcess
{
    public FakeHelperProcess(string pipeName, string token)
    {
        PipeName = pipeName;
        Token = token;
    }

    public string PipeName { get; }
    public string Token { get; }
    public bool IsRunning { get; private set; }
    public int LaunchCount { get; private set; }
    public int KillCount { get; private set; }

    public void Launch(string? helperPath = null, int watchdogSec = 15, string? logFilePath = null)
    {
        LaunchCount++;
        IsRunning = true;
    }

    public void Kill()
    {
        KillCount++;
        IsRunning = false;
    }

    /// <summary>Simulates the helper exiting on its own (e.g. after shutdown).</summary>
    public void SimulateExit() => IsRunning = false;

    /// <summary>
    /// Simulates a helper that takes a while to shut down (drains WinDivert,
    /// flushes its log), so the GUI's wait-for-exit path is exercised.
    /// </summary>
    public void SimulateExitAfter(TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            IsRunning = false;
        });
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// One-connection JSON-lines pipe server that answers every request with a
/// canned response, so the real <see cref="HelperIpcClient"/> framing is
/// exercised without any helper binary or elevation.
/// </summary>
internal sealed class ScriptedPipeServer : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly Func<string, string> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public ScriptedPipeServer(string name, Func<string, string> respond)
    {
        _respond = respond;
        _server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
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

                await writer.WriteLineAsync(_respond(line));
            }
        }
        catch
        {
            // best effort: the test owns teardown
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Dispose();
        _cts.Dispose();
    }
}
