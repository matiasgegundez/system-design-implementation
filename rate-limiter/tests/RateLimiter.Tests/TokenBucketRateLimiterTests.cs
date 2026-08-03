using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using Xunit;

namespace RateLimiter.Tests;

public sealed class TokenBucketRateLimiterTests
{
    private const string ClientId = "client-a";
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(1);

    [Fact]
    public void TryAcquire_AllowsRequestsWhileTokensAreAvailable()
    {
        var (limiter, _) = CreateLimiter(capacity: 3);

        var firstResult = limiter.TryAcquire(ClientId);
        var secondResult = limiter.TryAcquire(ClientId);
        var thirdResult = limiter.TryAcquire(ClientId);

        Assert.True(firstResult.IsAllowed);
        Assert.Equal(2, firstResult.RemainingTokens);
        Assert.Null(firstResult.RetryAfter);
        Assert.True(secondResult.IsAllowed);
        Assert.Equal(1, secondResult.RemainingTokens);
        Assert.Null(secondResult.RetryAfter);
        Assert.True(thirdResult.IsAllowed);
        Assert.Equal(0, thirdResult.RemainingTokens);
        Assert.Null(thirdResult.RetryAfter);
    }

    [Fact]
    public void TryAcquire_RejectsRequestWhenBucketIsEmpty()
    {
        var (limiter, _) = CreateLimiter(capacity: 1);
        Assert.True(limiter.TryAcquire(ClientId).IsAllowed);

        var result = limiter.TryAcquire(ClientId);

        Assert.False(result.IsAllowed);
        Assert.Equal(0, result.RemainingTokens);
        Assert.Equal(DefaultPeriod, result.RetryAfter);
    }

    [Fact]
    public void TryAcquire_ReplenishesTokenAfterEnoughTimeElapsed()
    {
        var (limiter, timeProvider) = CreateLimiter(capacity: 1);
        Assert.True(limiter.TryAcquire(ClientId).IsAllowed);

        timeProvider.Advance(DefaultPeriod);
        var result = limiter.TryAcquire(ClientId);

        Assert.True(result.IsAllowed);
        Assert.Equal(0, result.RemainingTokens);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public void TryAcquire_UpdatesRetryAfterAsTokensReplenish()
    {
        var replenishmentPeriod = TimeSpan.FromSeconds(10);
        var (limiter, timeProvider) = CreateLimiter(
            capacity: 1,
            tokensPerPeriod: 2,
            replenishmentPeriod);
        Assert.True(limiter.TryAcquire(ClientId).IsAllowed);

        var immediateResult = limiter.TryAcquire(ClientId);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        var partialResult = limiter.TryAcquire(ClientId);
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        var replenishedResult = limiter.TryAcquire(ClientId);

        Assert.False(immediateResult.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(5), immediateResult.RetryAfter);
        Assert.False(partialResult.IsAllowed);
        Assert.Equal(0, partialResult.RemainingTokens);
        Assert.Equal(TimeSpan.FromSeconds(3), partialResult.RetryAfter);
        Assert.True(replenishedResult.IsAllowed);
        Assert.Null(replenishedResult.RetryAfter);
    }

    [Fact]
    public void TryAcquire_AccumulatesFractionalTokensWithoutRoundingLoss()
    {
        var replenishmentPeriod = TimeSpan.FromSeconds(3);
        var (limiter, timeProvider) = CreateLimiter(
            capacity: 1,
            replenishmentPeriod: replenishmentPeriod);
        Assert.True(limiter.TryAcquire(ClientId).IsAllowed);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var firstPartialResult = limiter.TryAcquire(ClientId);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var secondPartialResult = limiter.TryAcquire(ClientId);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var fullTokenResult = limiter.TryAcquire(ClientId);

        Assert.False(firstPartialResult.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(2), firstPartialResult.RetryAfter);
        Assert.False(secondPartialResult.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(1), secondPartialResult.RetryAfter);
        Assert.True(fullTokenResult.IsAllowed);
    }

    [Fact]
    public void TryAcquire_DoesNotReplenishBeyondCapacity()
    {
        var (limiter, timeProvider) = CreateLimiter(capacity: 2);
        Assert.True(limiter.TryAcquire(ClientId).IsAllowed);

        timeProvider.Advance(DefaultPeriod * 10);
        var firstResult = limiter.TryAcquire(ClientId);
        var secondResult = limiter.TryAcquire(ClientId);
        var thirdResult = limiter.TryAcquire(ClientId);

        Assert.True(firstResult.IsAllowed);
        Assert.Equal(1, firstResult.RemainingTokens);
        Assert.True(secondResult.IsAllowed);
        Assert.Equal(0, secondResult.RemainingTokens);
        Assert.False(thirdResult.IsAllowed);
        Assert.Equal(DefaultPeriod, thirdResult.RetryAfter);
    }

    [Fact]
    public void TryAcquire_MaintainsIndependentBucketsForDifferentClients()
    {
        var (limiter, _) = CreateLimiter(capacity: 1);

        var firstClientResult = limiter.TryAcquire("client-a");
        var firstClientRejectedResult = limiter.TryAcquire("client-a");
        var secondClientResult = limiter.TryAcquire("client-b");
        var secondClientRejectedResult = limiter.TryAcquire("client-b");

        Assert.True(firstClientResult.IsAllowed);
        Assert.False(firstClientRejectedResult.IsAllowed);
        Assert.True(secondClientResult.IsAllowed);
        Assert.False(secondClientRejectedResult.IsAllowed);
    }

    [Fact]
    public void TryAcquire_TreatsClientIdsAsCaseSensitive()
    {
        var (limiter, _) = CreateLimiter(capacity: 1);

        var lowerCaseResult = limiter.TryAcquire("client");
        var upperCaseResult = limiter.TryAcquire("Client");

        Assert.True(lowerCaseResult.IsAllowed);
        Assert.True(upperCaseResult.IsAllowed);
    }

    [Fact]
    public void TryAcquire_FloorsFractionalRemainingTokens()
    {
        var (limiter, timeProvider) = CreateLimiter(
            capacity: 2,
            replenishmentPeriod: TimeSpan.FromSeconds(2));
        Assert.True(limiter.TryAcquire(ClientId).IsAllowed);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = limiter.TryAcquire(ClientId);

        Assert.True(result.IsAllowed);
        Assert.Equal(0, result.RemainingTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenCapacityIsNotPositive_Throws(int capacity)
    {
        var options = CreateOptions(capacity: capacity);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TokenBucketRateLimiter(options, new FakeTimeProvider()));

        Assert.Equal(nameof(RateLimitOptions.Capacity), exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenTokensPerPeriodIsNotPositive_Throws(int tokensPerPeriod)
    {
        var options = CreateOptions(tokensPerPeriod: tokensPerPeriod);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TokenBucketRateLimiter(options, new FakeTimeProvider()));

        Assert.Equal(nameof(RateLimitOptions.TokensPerPeriod), exception.ParamName);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Constructor_WhenReplenishmentPeriodIsNotPositive_Throws(long periodTicks)
    {
        var options = CreateOptions(
            replenishmentPeriod: TimeSpan.FromTicks(periodTicks));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TokenBucketRateLimiter(options, new FakeTimeProvider()));

        Assert.Equal(nameof(RateLimitOptions.ReplenishmentPeriod), exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenOptionsIsNull_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new TokenBucketRateLimiter(null!, new FakeTimeProvider()));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenTimeProviderIsNull_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new TokenBucketRateLimiter(CreateOptions(), null!));

        Assert.Equal("timeProvider", exception.ParamName);
    }

    [Fact]
    public void TryAcquire_WhenClientIdIsNull_Throws()
    {
        var (limiter, _) = CreateLimiter();

        var exception = Assert.Throws<ArgumentNullException>(
            () => limiter.TryAcquire(null!));

        Assert.Equal("clientId", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void TryAcquire_WhenClientIdIsBlank_Throws(string clientId)
    {
        var (limiter, _) = CreateLimiter();

        var exception = Assert.Throws<ArgumentException>(
            () => limiter.TryAcquire(clientId));

        Assert.Equal(nameof(clientId), exception.ParamName);
    }

    [Fact]
    public void TryAcquire_RoundsRetryAfterUpToNextTick()
    {
        var (limiter, _) = CreateLimiter(
            capacity: 1,
            tokensPerPeriod: 2,
            replenishmentPeriod: TimeSpan.FromTicks(3));
        Assert.True(limiter.TryAcquire(ClientId).IsAllowed);

        var result = limiter.TryAcquire(ClientId);

        Assert.False(result.IsAllowed);
        Assert.Equal(TimeSpan.FromTicks(2), result.RetryAfter);
    }

    [Fact]
    public async Task TryAcquire_ConcurrentRequestsDoNotExceedCapacity()
    {
        const int capacity = 25;
        const int requestCount = 200;
        var (limiter, _) = CreateLimiter(capacity: capacity);
        var startGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var requests = Enumerable.Range(0, requestCount)
            .Select(_ => Task.Run(async () =>
            {
                await startGate.Task;
                return limiter.TryAcquire(ClientId);
            }))
            .ToArray();

        startGate.SetResult(true);
        var results = await Task.WhenAll(requests);

        Assert.Equal(capacity, results.Count(result => result.IsAllowed));
        Assert.Equal(
            requestCount - capacity,
            results.Count(result => !result.IsAllowed));
    }

    private static (
        TokenBucketRateLimiter Limiter,
        FakeTimeProvider TimeProvider) CreateLimiter(
        int capacity = 3,
        int tokensPerPeriod = 1,
        TimeSpan? replenishmentPeriod = null)
    {
        var timeProvider = new FakeTimeProvider();
        var limiter = new TokenBucketRateLimiter(
            CreateOptions(capacity, tokensPerPeriod, replenishmentPeriod),
            timeProvider);

        return (limiter, timeProvider);
    }

    private static RateLimitOptions CreateOptions(
        int capacity = 3,
        int tokensPerPeriod = 1,
        TimeSpan? replenishmentPeriod = null) =>
        new()
        {
            Capacity = capacity,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = replenishmentPeriod ?? DefaultPeriod
        };
}
