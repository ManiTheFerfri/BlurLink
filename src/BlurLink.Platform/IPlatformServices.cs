namespace BlurLink.Platform;

/// <summary>Shell-implemented OS interactions the view-models need. Implemented
/// once for Windows in BlurLink.Shell; faked in tests. Three methods on purpose —
/// grow this only with a recorded reason, never speculatively.</summary>
public interface IPlatformServices
{
    void CopyToClipboard(string text);
    Task<string?> PickExeFileAsync(string initialPath);
    void OpenFolder(string path);
}
