using BlurLink.Platform;

namespace BlurLink.Shell.Services;

/// <summary>Null-object <see cref="IPlatformServices"/>: throws
/// <see cref="InvalidOperationException"/> on every member. Shared by the
/// view-models constructed without an explicit platform (older and test
/// constructions); production always passes the real one.</summary>
internal sealed class ThrowingPlatformServices : IPlatformServices
{
    public void CopyToClipboard(string text) => throw new InvalidOperationException("No IPlatformServices was provided.");
    public Task<string?> PickExeFileAsync(string initialPath) => throw new InvalidOperationException("No IPlatformServices was provided.");
    public void OpenFolder(string path) => throw new InvalidOperationException("No IPlatformServices was provided.");
}
