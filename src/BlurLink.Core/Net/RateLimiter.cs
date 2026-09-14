namespace BlurLink.Core.Net;

/// <summary>
/// Token-bucket rate limiter: default 10 forwarded packets/sec, burst 20.
/// Thread-safe. Used by the GUI (pre-flight estimate) and mirrored natively.
/// </summary>
public sealed class RateLimiter
{
    private readonly double _refillPerSecond;
    private readonly double _maxTokens;
    private double _tokens;
    private long _lastTick;
    private readonly object _lock = new();

    public int RejectedCount { get; private set; }

    public RateLimiter(int perSecond, int burst)
    {
        if (perSecond < 1 || perSecond > Contracts.BlurLinkConstants.MaxRateLimitPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(perSecond),
                $"Rate limit must be 1-{Contracts.BlurLinkConstants.MaxRateLimitPerSecond}/s.");
        }

        if (burst < 1 || burst > Contracts.BlurLinkConstants.MaxRateLimitBurst)
        {
            throw new ArgumentOutOfRangeException(nameof(burst),
                $"Burst must be 1-{Contracts.BlurLinkConstants.MaxRateLimitBurst}.");
        }

        _refillPerSecond = perSecond;
        _maxTokens = burst;
        _tokens = burst;
        _lastTick = Environment.TickCount64;
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }

            RejectedCount++;
            return false;
        }
    }

    private void Refill()
    {
        var now = Environment.TickCount64;
        var elapsedSec = (now - _lastTick) / 1000.0;
        _lastTick = now;
        if (elapsedSec > 0)
        {
            _tokens = Math.Min(_maxTokens, _tokens + elapsedSec * _refillPerSecond);
        }
    }
}
