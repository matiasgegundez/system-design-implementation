using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using Xunit;

namespace RateLimiter.Tests;

public sealed class RateLimiterApiTests
{
    [Fact]
    public async Task PostRequest_WhenTokenIsAvailable_ReturnsOk()
    {
        using var factory = new RateLimiterApiFactory(capacity: 2);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.RetryAfter);

        var result = await response.Content.ReadFromJsonAsync<RateLimitResult>(
            cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsAllowed);
        Assert.Equal(1, result.RemainingTokens);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task PostRequest_WhenBucketIsEmpty_ReturnsTooManyRequests()
    {
        using var factory = new RateLimiterApiFactory(
            capacity: 1,
            replenishmentPeriod: TimeSpan.FromMilliseconds(1500));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var allowedResponse = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);

        using var response = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter?.Delta);

        var result = await response.Content.ReadFromJsonAsync<RateLimitResult>(
            cancellationToken);

        Assert.NotNull(result);
        Assert.False(result.IsAllowed);
        Assert.Equal(0, result.RemainingTokens);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), result.RetryAfter);
    }

    [Fact]
    public async Task PostRequest_AfterEnoughTimePasses_ReturnsOk()
    {
        var replenishmentPeriod = TimeSpan.FromSeconds(10);
        using var factory = new RateLimiterApiFactory(
            capacity: 1,
            replenishmentPeriod: replenishmentPeriod);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var firstResponse = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);
        using var rejectedResponse = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);

        factory.TimeProvider.Advance(replenishmentPeriod);

        using var response = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RateLimitResult>(
            cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsAllowed);
        Assert.Equal(0, result.RemainingTokens);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task PostRequest_UsesIndependentBucketsForDifferentClients()
    {
        using var factory = new RateLimiterApiFactory(capacity: 1);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var firstClientResponse = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);
        using var firstClientRejectedResponse = await client.PostAsync(
            "/api/requests/client-a",
            content: null,
            cancellationToken);
        using var secondClientResponse = await client.PostAsync(
            "/api/requests/client-b",
            content: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstClientResponse.StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            firstClientRejectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondClientResponse.StatusCode);
    }

    [Fact]
    public async Task PostRequest_WithWhitespaceClientId_ReturnsBadRequest()
    {
        using var factory = new RateLimiterApiFactory();
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.PostAsync(
            "/api/requests/%20",
            content: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            cancellationToken);

        Assert.NotNull(problem);
        Assert.Equal("Invalid client identifier", problem.Title);
        Assert.Equal(
            "Client identifier must not be empty or whitespace.",
            problem.Detail);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
    }

    [Fact]
    public async Task PostRequest_WithoutClientId_ReturnsNotFound()
    {
        using var factory = new RateLimiterApiFactory();
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.PostAsync(
            "/api/requests/",
            content: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class RateLimiterApiFactory : WebApplicationFactory<Program>
    {
        private readonly RateLimitOptions _options;

        public RateLimiterApiFactory(
            int capacity = 2,
            int tokensPerPeriod = 1,
            TimeSpan? replenishmentPeriod = null)
        {
            _options = new RateLimitOptions
            {
                Capacity = capacity,
                TokensPerPeriod = tokensPerPeriod,
                ReplenishmentPeriod =
                    replenishmentPeriod ?? TimeSpan.FromSeconds(10)
            };
        }

        public FakeTimeProvider TimeProvider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRateLimiter>();
                services.AddSingleton<IRateLimiter>(
                    new TokenBucketRateLimiter(_options, TimeProvider));
            });
        }
    }
}
