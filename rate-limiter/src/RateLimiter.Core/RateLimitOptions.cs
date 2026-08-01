namespace RateLimiter.Core;

public sealed class RateLimitOptions
{
    public int Capacity { get; init; }

    public int TokensPerPeriod { get; init; }

    public TimeSpan ReplenishmentPeriod { get; init; }
}
