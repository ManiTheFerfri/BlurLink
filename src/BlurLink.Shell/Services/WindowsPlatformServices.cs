using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using BlurLink.Platform;

namespace BlurLink.Shell.Services;

public sealed class WindowsPlatformServices(Func<TopLevel?> topLevel) : IPlatformServices
{
    public void CopyToClipboard(string text)
    {
        var clipboard = topLevel()?.Clipboard;
        if (clipboard is not null)
        {
            _ = clipboard.SetTextAsync(text);
        }
    }

    public async Task<string?> PickExeFileAsync(string initialPath)
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var exeType = new FilePickerFileType("Executables")
        {
            Patterns = new[] { "*.exe" },
        };
        var suggestedStartLocation = await TryGetStartFolderAsync(storage, initialPath);
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate Blur.exe",
            AllowMultiple = false,
            FileTypeFilter = new[] { exeType },
            SuggestedStartLocation = suggestedStartLocation,
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private static async Task<Avalonia.Platform.Storage.IStorageFolder?> TryGetStartFolderAsync(
        Avalonia.Platform.Storage.IStorageProvider storage, string initialPath)
    {
        var directory = string.IsNullOrWhiteSpace(initialPath) ? null : Path.GetDirectoryName(initialPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return await storage.TryGetFolderFromPathAsync(directory);
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
