namespace RateLimiter.Core;

public sealed record RateLimitResult(
    bool IsAllowed,
    int RemainingTokens,
    TimeSpan? RetryAfter);

