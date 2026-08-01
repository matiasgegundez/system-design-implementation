namespace RateLimiter.Core;

public interface IRateLimiter
{
    RateLimitResult TryAcquire(string clientId);
}

