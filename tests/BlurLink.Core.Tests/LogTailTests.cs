using BlurLink.Platform;
using BlurLink.Desktop.ViewModels;
using BlurLink.Core.Logging;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Bounded tail reader for the helper-log diagnostics viewer: end-seek scan,
/// no writer locks, never throws on missing/locked/empty files.
/// </summary>
public sealed class LogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string PathFor(string name) => Path.Combine(_dir, name);

    public LogTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MissingFile_YieldsEmptyTail()
    {
        Assert.Empty(LogTail.Read(PathFor("nope.log")));
    }

    [Fact]
    public void EmptyFile_YieldsEmptyTail()
    {
        var path = PathFor("empty.log");
        File.WriteAllText(path, string.Empty);
        Assert.Empty(LogTail.Read(path));
    }

    [Fact]
    public void ReturnsLastLines_NewestLast()
    {
        var path = PathFor("app.log");
        File.WriteAllLines(path, Enumerable.Range(1, 50).Select(i => $"line-{i:000}"));
        var tail = LogTail.Read(path, maxLines: 10);

        Assert.Equal(10, tail.Count);
        Assert.Equal("line-041", tail[0]);
        Assert.Equal("line-050", tail[^1]);
    }

    [Fact]
    public void WholeFile_WhenShorterThanMax()
    {
        var path = PathFor("small.log");
        File.WriteAllLines(path, new[] { "a", "b", "c" });
        var tail = LogTail.Read(path, maxLines: 10);

        Assert.Equal(3, tail.Count);
        Assert.Equal("a", tail[0]);
        Assert.Equal("c", tail[^1]);
    }

    [Fact]
    public void HandlesUnixAndWindowsLineEndings()
    {
        var path = PathFor("mixed.log");
        File.WriteAllText(path, "one\ntwo\r\nthree\n");
        var tail = LogTail.Read(path);

        Assert.Equal(new[] { "one", "two", "three" }, tail);
    }

    [Fact]
    public void HandlesFileWithoutTrailingNewline()
    {
        var path = PathFor("notrailing.log");
        File.WriteAllText(path, "a\nb\nc");
        Assert.Equal(new[] { "a", "b", "c" }, LogTail.Read(path));
    }

    [Fact]
    public void PartialFirstLine_IsDropped()
    {
        // The 256KB end-seek window must start mid-file: the first chunk is
        // a truncated line and must never surface as garbage.
        var path = PathFor("partial.log");
        File.WriteAllText(path, new string('A', 300_000) + "\ncomplete-1\ncomplete-2\n");
        Assert.Equal(new[] { "complete-1", "complete-2" }, LogTail.Read(path));
    }

    [Fact]
    public void FileBeingWritten_IsReadableWithoutLocks()
    {
        var path = PathFor("live.log");
        File.WriteAllText(path, "first\n");
        using (var writer = new StreamWriter(path, append: true))
        {
            writer.WriteLine("second");
            writer.Flush();

            // Reader must succeed while the writer holds the file open.
            var tail = LogTail.Read(path);
            Assert.Equal(new[] { "first", "second" }, tail);

            writer.WriteLine("third");
            writer.Flush();
        }
    }

    [Fact]
    public void HugeFile_TailIsBoundedAndFast()
    {
        var path = PathFor("huge.log");
        using (var writer = new StreamWriter(path))
        {
            for (var i = 1; i <= 20_000; i++)
            {
                writer.WriteLine($"entry-{i:00000} some metadata line");
            }
        }

        var tail = LogTail.Read(path, maxLines: 50);
        Assert.Equal(50, tail.Count);
        Assert.StartsWith("entry-19951", tail[0]);
        Assert.StartsWith("entry-20000", tail[^1]);
    }

    [Fact]
    public void ZeroMaxLines_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogTail.Read(PathFor("x.log"), 0));
    }

    [Fact]
    public void FormatForDiagnostics_HasHeader_AndHandlesEmpty()
    {
        var formatted = LogTail.FormatForDiagnostics(@"C:\logs\helper.log", Array.Empty<string>());
        Assert.Contains("helper.log", formatted);
        Assert.Contains("no log lines", formatted);

        var withLines = LogTail.FormatForDiagnostics(@"C:\logs\helper.log", new[] { "l1", "l2" });
        Assert.Contains("l1", withLines);
        Assert.EndsWith("l2" + Environment.NewLine, withLines);
    }
}

/// <summary>Settings-tab wiring for the helper-log viewer.</summary>
public sealed class SettingsHelperLogTests : IDisposable
{
    private readonly MainViewModel _vm = new();

    public SettingsHelperLogTests()
    {
        _vm.CurrentView = "Settings";
        _vm.Settings.IsSettingsTabVisible = true;
    }

    public void Dispose()
    {
        _vm.Settings.Dispose();
        _vm.Join.Dispose();
        _vm.Dispose();
    }

    [Fact]
    public void RefreshHelperLog_PopulatesStatus_WithoutThrowing()
    {
        _vm.Settings.RefreshHelperLog();
        Assert.False(string.IsNullOrEmpty(_vm.Settings.HelperLogStatus));
        // Two valid states: no helper ever ran on this machine (empty list,
        // informative status) — or a real helper.log exists because BlurLink
        // ran here before (dev/CI boxes that launched the app). Both must
        // read cleanly; the old hard Assert.Empty broke on used machines.
        if (_vm.Settings.HelperLogLines.Count == 0)
        {
            Assert.Contains("No helper log yet", _vm.Settings.HelperLogStatus);
        }
        else
        {
            Assert.Contains("newest last", _vm.Settings.HelperLogStatus);
        }
    }

    [Fact]
    public void RefreshCommand_Executes()
    {
        _vm.Settings.RefreshHelperLogCommand.Execute(null);
        Assert.False(string.IsNullOrEmpty(_vm.Settings.HelperLogStatus));
    }

    [Fact]
    public void RefreshHelperLogCommand_Exists()
    {
        Assert.NotNull(_vm.Settings.RefreshHelperLogCommand);
        Assert.NotNull(_vm.Settings.CopyHelperLogCommand);
    }

    [Fact]
    public void DefaultHelperLogPath_IsUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlurLink", "logs", "helper.log");
        Assert.Equal(expected, HelperLauncher.DefaultHelperLogPath());
    }
}
