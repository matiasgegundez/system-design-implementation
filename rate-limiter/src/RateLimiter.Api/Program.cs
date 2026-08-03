using System.Globalization;
using RateLimiter.Core;

var builder = WebApplication.CreateBuilder(args);

var rateLimitOptions = builder.Configuration
    .GetRequiredSection("RateLimit")
    .Get<RateLimitOptions>()
    ?? throw new InvalidOperationException("Rate limit configuration is required.");

var rateLimiter = new TokenBucketRateLimiter(
    rateLimitOptions,
    TimeProvider.System);

builder.Services.AddSingleton<IRateLimiter>(rateLimiter);

var app = builder.Build();

app.MapPost("/api/requests/{clientId}", HandleRequest);

app.Run();

static IResult HandleRequest(
    string clientId,
    IRateLimiter rateLimiter,
    HttpContext httpContext)
{
    if (string.IsNullOrWhiteSpace(clientId))
    {
        return Results.Problem(
            title: "Invalid client identifier",
            detail: "Client identifier must not be empty or whitespace.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var result = rateLimiter.TryAcquire(clientId);

    if (result.IsAllowed)
    {
        return Results.Ok(result);
    }

    if (result.RetryAfter is { } retryAfter)
    {
        var retryAfterSeconds = Math.Max(
            1L,
            (long)Math.Ceiling(retryAfter.TotalSeconds));

        httpContext.Response.Headers["Retry-After"] =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
    }

    return Results.Json(
        result,
        statusCode: StatusCodes.Status429TooManyRequests);
}

public partial class Program
{
}
