using System.Collections.Concurrent;

namespace RateLimiter.Core;

public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, BucketState> _buckets =
        new(StringComparer.Ordinal);

    // Fixed-point credit avoids cumulative rounding errors when replenishing
    // fractional tokens. One full token equals ReplenishmentPeriod.Ticks credit units.
    private readonly decimal _capacityCredit;
    private readonly decimal _creditUnitsPerToken;
    private readonly int _tokensPerPeriod;
    private readonly TimeProvider _timeProvider;

    public TokenBucketRateLimiter(
        RateLimitOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.Capacity),
                options.Capacity,
                "Capacity must be greater than zero.");
        }

        if (options.TokensPerPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.TokensPerPeriod),
                options.TokensPerPeriod,
                "Tokens per period must be greater than zero.");
        }

        if (options.ReplenishmentPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ReplenishmentPeriod),
                options.ReplenishmentPeriod,
                "Replenishment period must be greater than zero.");
        }

        _creditUnitsPerToken = options.ReplenishmentPeriod.Ticks;
        _capacityCredit = options.Capacity * _creditUnitsPerToken;
        _tokensPerPeriod = options.TokensPerPeriod;
        _timeProvider = timeProvider;
    }

    public RateLimitResult TryAcquire(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var bucket = _buckets.GetOrAdd(
            clientId,
            _ => new BucketState(_capacityCredit, _timeProvider.GetTimestamp()));

        lock (bucket.SyncRoot)
        {
            var currentTimestamp = _timeProvider.GetTimestamp();
            Refill(bucket, currentTimestamp);

            if (bucket.AvailableCredit >= _creditUnitsPerToken)
            {
                bucket.AvailableCredit -= _creditUnitsPerToken;

                return new RateLimitResult(
                    IsAllowed: true,
                    RemainingTokens: ToWholeTokens(bucket.AvailableCredit),
                    RetryAfter: null);
            }

            var missingCredit = _creditUnitsPerToken - bucket.AvailableCredit;
            var retryAfterTicks = decimal.Ceiling(
                missingCredit / _tokensPerPeriod);

            return new RateLimitResult(
                IsAllowed: false,
                RemainingTokens: 0,
                RetryAfter: TimeSpan.FromTicks(decimal.ToInt64(retryAfterTicks)));
        }
    }

    private void Refill(BucketState bucket, long currentTimestamp)
    {
        var elapsed = _timeProvider.GetElapsedTime(
            bucket.LastRefillTimestamp,
            currentTimestamp);

        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var replenishedCredit = (decimal)elapsed.Ticks * _tokensPerPeriod;

        bucket.AvailableCredit = Math.Min(
            _capacityCredit,
            bucket.AvailableCredit + replenishedCredit);
        bucket.LastRefillTimestamp = currentTimestamp;
    }

    private int ToWholeTokens(decimal availableCredit) =>
        decimal.ToInt32(decimal.Floor(availableCredit / _creditUnitsPerToken));

    private sealed class BucketState
    {
        public BucketState(decimal availableCredit, long lastRefillTimestamp)
        {
            AvailableCredit = availableCredit;
            LastRefillTimestamp = lastRefillTimestamp;
        }

        public object SyncRoot { get; } = new();

        public decimal AvailableCredit { get; set; }

        public long LastRefillTimestamp { get; set; }
    }
}
