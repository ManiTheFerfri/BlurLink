using BlurLink.Core.Net;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class RateLimiterTests
{
    [Fact]
    public void Burst_AllowsImmediateBurst_ThenThrottles()
    {
        var limiter = new RateLimiter(perSecond: 10, burst: 20);
        for (var i = 0; i < 20; i++)
        {
            Assert.True(limiter.TryAcquire());
        }

        Assert.False(limiter.TryAcquire());
        Assert.Equal(1, limiter.RejectedCount);
    }

    [Fact]
    public void Refills_OverTime()
    {
        var limiter = new RateLimiter(perSecond: 50, burst: 2);
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
        Thread.Sleep(150); // ~7 tokens at 50/s
        Assert.True(limiter.TryAcquire());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(101, 20)]
    [InlineData(10, 0)]
    [InlineData(10, 201)]
    public void OutOfRange_Rejected(int perSecond, int burst)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimiter(perSecond, burst));
    }
}
