using BlurLink.Desktop.Services;
using Xunit;

namespace BlurLink.Core.Tests;

/// <summary>
/// Two BlurLink windows must never coexist: each can launch its own elevated
/// helper, and two helpers would both divert the same broadcasts. The guard is
/// a named event, so "createdNew" is a deterministic signal and a released
/// instance leaves no stale lock behind.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    private static string UniqueName() => @"Local\BlurLink.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void SecondInstance_IsRefused_UntilTheFirstReleases()
    {
        var name = UniqueName();

        using (var first = SingleInstanceGuard.Acquire(name))
        using (var second = SingleInstanceGuard.Acquire(name))
        {
            Assert.True(first.IsAcquired);
            Assert.False(second.IsAcquired);
        }

        // Releasing the first instance frees the slot (no stale lock).
        using var third = SingleInstanceGuard.Acquire(name);
        Assert.True(third.IsAcquired);
    }

    [Fact]
    public void DistinctNames_DoNotCollide()
    {
        using var a = SingleInstanceGuard.Acquire(UniqueName());
        using var b = SingleInstanceGuard.Acquire(UniqueName());

        Assert.True(a.IsAcquired);
        Assert.True(b.IsAcquired);
    }
}
