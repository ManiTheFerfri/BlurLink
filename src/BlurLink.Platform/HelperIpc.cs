using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BlurLink.Contracts;

namespace BlurLink.Platform;

/// <summary>
/// The subset of the elevated helper's lifecycle the GUI needs. Extracted so
/// the Join view-model can be driven by a test double without launching a
/// real, elevated process.
/// </summary>
public interface IHelperProcess : IDisposable
{
    string PipeName { get; }
    string Token { get; }
    bool IsRunning { get; }
    void Launch(string? helperPath = null, int watchdogSec = 15, string? logFilePath = null);
    void Kill();
}

/// <summary>
/// Launches the elevated helper (blurlink-net.exe) via UAC ("runas") with a
/// random pipe name + capability token, then talks to it over a local named
/// pipe. The GUI itself is never elevated.
/// </summary>
public sealed class HelperLauncher : IHelperProcess
{
    private System.Diagnostics.Process? _process;
    private bool _disposed;

    public string PipeName { get; private set; } = string.Empty;
    public string Token { get; private set; } = string.Empty;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Files shipped inside BlurLink.exe (portable) and staged to the bin dir:
    /// the helper plus the WinDivert runtime. Resource name → file name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> EmbeddedFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BlurLink.Desktop.Native.blurlink-net.exe"] = BlurLinkConstants.HelperExeName,
            ["BlurLink.Desktop.Native.WinDivert.dll"] = "WinDivert.dll",
            ["BlurLink.Desktop.Native.WinDivert64.sys"] = "WinDivert64.sys",
        };

    public const string EmbeddedHelperResource = "BlurLink.Desktop.Native.blurlink-net.exe";

    public static bool HasEmbeddedHelper
        => typeof(HelperLauncher).Assembly.GetManifestResourceNames().Contains(EmbeddedHelperResource);

    /// <summary>All bridge files embedded (helper + WinDivert runtime)?</summary>
    public static bool HasEmbeddedBridgeFiles
        => EmbeddedFiles.Keys.All(k =>
            typeof(HelperLauncher).Assembly.GetManifestResourceNames().Contains(k));

    /// <summary>Side-by-side dev layout: helper next to the GUI exe.</summary>
    public static string SideBySidePath()
    {
        var dir = AppContext.BaseDirectory;
        return Path.Combine(dir, BlurLinkConstants.HelperExeName);
    }

    /// <summary>User-local staging dir for helper + WinDivert (no admin to write).</summary>
    public static string BinDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlurLink", "bin");

    /// <summary>Default dashboard log for the elevated helper (metadata only).</summary>
    public static string DefaultHelperLogPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlurLink", "logs", "helper.log");

    /// <summary>True when a bridge can be launched: everything staged or embeddable.</summary>
    public static bool HelperAvailable
    {
        get
        {
            if (HasEmbeddedBridgeFiles)
            {
                return true;
            }

            return BridgeFilesPresent(BinDir()) || BridgeFilesPresent(AppContext.BaseDirectory);
        }
    }

    private static bool BridgeFilesPresent(string dir)
        => File.Exists(Path.Combine(dir, BlurLinkConstants.HelperExeName))
           && File.Exists(Path.Combine(dir, "WinDivert.dll"))
           && File.Exists(Path.Combine(dir, "WinDivert64.sys"));

    /// <summary>
    /// Stages every embedded bridge file to the bin dir (skips identical ones).
    /// Returns the helper path. Throws InvalidDataException on bad payloads.
    /// </summary>
    public static string EnsureStagedBinaries()
    {
        var asm = typeof(HelperLauncher).Assembly;
        var names = asm.GetManifestResourceNames();
        var staged = new List<string>();
        string helperPath = Path.Combine(BinDir(), BlurLinkConstants.HelperExeName);

        foreach (var (resource, file) in EmbeddedFiles)
        {
            if (!names.Contains(resource, StringComparer.Ordinal))
            {
                continue; // dev build without this file staged; side-by-side may cover it
            }

            using var stream = asm.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded resource missing: {resource}");
            var (dest, written) = ExtractStagedFile(stream, Path.Combine(BinDir(), file));
            if (written)
            {
                staged.Add(file);
            }

            if (string.Equals(file, BlurLinkConstants.HelperExeName, StringComparison.OrdinalIgnoreCase))
            {
                helperPath = dest;
            }
        }

        Core.Logging.AppLog.Info(staged.Count == 0
            ? "Bridge files already staged."
            : "Staged bridge files: " + string.Join(", ", staged));
        return helperPath;
    }

    /// <summary>
    /// Resolves the helper exe path, staging embedded files first.
    /// Prefers embedded (portable single-exe), then staged/side-by-side.
    /// </summary>
    public string HelperPath()
    {
        if (HasEmbeddedBridgeFiles)
        {
            return EnsureStagedBinaries();
        }

        foreach (var dir in new[] { BinDir(), AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(dir, BlurLinkConstants.HelperExeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return SideBySidePath(); // Launch() will raise the clear FileNotFound error.
    }

    /// <summary>
    /// Writes one staged binary atomically (skips when identical).
    /// Validates the MZ header so garbage can never be launched elevated.
    /// Returns (destPath, written).
    /// </summary>
    public static (string Path, bool Written) ExtractStagedFile(Stream source, string destPath)
    {
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new InvalidDataException($"Staged file is not a valid executable: {destPath} (MZ header missing).");
        }

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(destPath) && File.ReadAllBytes(destPath).SequenceEqual(bytes))
        {
            return (destPath, false); // already current; avoid touching it
        }

        // Write atomically so a half-written exe can never be launched.
        var tmp = destPath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, destPath, overwrite: true);
        return (destPath, true);
    }

    public void Launch(string? helperPath = null, int watchdogSec = 15, string? logFilePath = null)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Helper is already running.");
        }

        helperPath ??= HelperPath();
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException(
                $"Helper not found: {helperPath}. Build blurlink-net.exe first (see scripts/build.ps1).",
                helperPath);
        }

        if (watchdogSec is < 2 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(watchdogSec), "Watchdog must be 2-300s.");
        }

        PipeName = IpcProtocol.CreatePipeName();
        Token = IpcProtocol.CreateToken();
        logFilePath ??= DefaultHelperLogPath();

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = $"--pipe \"{PipeName}\" --token \"{Token}\" --watchdog-sec {watchdogSec} --log-file \"{logFilePath}\"",
            UseShellExecute = true,
            Verb = "runas", // UAC elevation for the helper only
            WorkingDirectory = Path.GetDirectoryName(helperPath) ?? string.Empty,
        };

        try
        {
            _process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start helper process.");
            // Never log pipe name or token: knowledge of both equals control.
            Core.Logging.AppLog.Info(
                $"Helper launched (pid={_process.Id}, watchdog={watchdogSec}s, path='{helperPath}').");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user cancelled UAC.
            PipeName = string.Empty;
            Token = string.Empty;
            Core.Logging.AppLog.Info("Helper launch cancelled at UAC prompt.");
            throw new OperationCanceledException("Elevation was cancelled. The bridge was not started.", ex);
        }
    }

    /// <summary>Force-kills the helper (WinDivert handle dies with it).</summary>
    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Kill();
    }
}

/// <summary>
/// Minimal JSON-lines client for the helper's named pipe.
/// Thread-safe: concurrent callers (status poll + user actions) are
/// serialized so request/response framing can never interleave.
/// </summary>
public sealed class HelperIpcClient : IDisposable
{
    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public bool Connected => _pipe?.IsConnected == true;

    /// <summary>
    /// Connects, retrying while <paramref name="stillAlive"/> reports the
    /// helper is starting up (covers slow UAC approval). Throws on timeout.
    /// </summary>
    public async Task ConnectAsync(
        string pipeName,
        CancellationToken ct,
        Func<bool>? stillAlive = null,
        int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 300));
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (stillAlive is not null && !stillAlive())
            {
                throw new IOException(
                    "Helper process exited before the pipe was ready.",
                    lastError);
            }

            DisposePipe();
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await _pipe.ConnectAsync(1000, ct).ConfigureAwait(false);
                _reader = new StreamReader(_pipe, NoBomUtf8, leaveOpen: true);
                _writer = new StreamWriter(_pipe, NoBomUtf8, leaveOpen: true) { AutoFlush = true };
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                lastError = ex;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            "Timed out waiting for the helper pipe. The helper may have failed to start.", lastError);
    }

    public async Task<string> SendAsync(object message, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writer is null || _reader is null)
            {
                throw new InvalidOperationException("Not connected.");
            }

            var json = JsonSerializer.Serialize(message);
            var kind = message.GetType().Name; // type only: payload may carry the token
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            var response = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            Core.Logging.AppLog.Debug($"IPC {kind} → {SummarizeResponse(response)}");
            return response ?? throw new IOException("Helper closed the connection.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DisposePipe()
    {
        _writer?.Dispose();
        _writer = null;
        _reader?.Dispose();
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    /// <summary>One-line response summary for logs (never payloads/tokens).</summary>
    private static string SummarizeResponse(string? response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return "(empty)";
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "?";
            var extra = string.Empty;
            if (root.TryGetProperty("active", out var a))
            {
                extra += $" active={a}";
            }

            foreach (var key in new[] { "captured", "forwarded", "dropped", "message", "lastError" })
            {
                if (root.TryGetProperty(key, out var v))
                {
                    extra += $" {key}={v}";
                }
            }

            return $"{type}{extra}".Trim();
        }
        catch
        {
            return $"(unparseable {response.Length} chars)";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposePipe();
        _gate.Dispose();
    }
}
