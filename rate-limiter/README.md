# Token Bucket Rate Limiter

A reusable, in-memory Token Bucket rate limiter built with C# and .NET 8. Each
client identifier has an independent bucket. The ASP.NET Core project is a
minimal executable adapter around a core component that is independent of
ASP.NET Core.

This component limits request frequency over time. It does not limit how many
operations may execute concurrently.

## Behavior

- A new client starts with a full bucket and may use an initial burst of up to
  `Capacity` requests.
- Each accepted request consumes one token.
- Tokens are replenished continuously according to `TokensPerPeriod` and
  `ReplenishmentPeriod`.
- Replenishment is calculated when a request arrives; there is no background
  timer.
- A bucket never exceeds its configured capacity.
- A request is rejected when less than one complete token is available.
- Client identifiers are compared literally and are case-sensitive.
- Bucket state exists only in the memory of the current API process.

## Repository structure

```text
rate-limiter/
├── src/
│   ├── RateLimiter.Core/   # Reusable rate-limiting component
│   └── RateLimiter.Api/    # Minimal ASP.NET Core HTTP adapter
├── tests/
│   └── RateLimiter.Tests/  # Core and HTTP integration tests
├── RateLimiter.sln
├── README.md
└── DESIGN.md
```

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- An HTTP client such as `curl` for manual requests

Redis, a database, Docker, and other external services are not required.

Confirm the installed SDK with:

```shell
dotnet --version
```

## Configuration

The default policy is defined in
`src/RateLimiter.Api/appsettings.json`:

```json
{
  "RateLimit": {
    "Capacity": 5,
    "TokensPerPeriod": 5,
    "ReplenishmentPeriod": "00:01:00"
  }
}
```

| Setting | Meaning |
| --- | --- |
| `Capacity` | Maximum tokens in each client bucket and its initial token count |
| `TokensPerPeriod` | Tokens replenished during one replenishment period |
| `ReplenishmentPeriod` | Duration over which `TokensPerPeriod` tokens are replenished |

The default policy permits an initial burst of five requests per client and
then replenishes continuously at five tokens per minute, equivalent to one
token every 12 seconds. Configuration is read at startup. Missing or
non-positive values cause startup to fail explicitly.

## Build

Run these commands from the `rate-limiter` directory:

```shell
dotnet restore RateLimiter.sln
dotnet build RateLimiter.sln --no-restore
```

## Run

The following command uses a stable HTTP address and does not depend on a local
HTTPS development certificate:

```shell
dotnet run --project src/RateLimiter.Api/RateLimiter.Api.csproj --no-launch-profile -- --urls http://localhost:5000
```

Stop the application with `Ctrl+C`.

## Test

After building the solution, run:

```shell
dotnet test RateLimiter.sln --no-build --no-restore
```

The suite contains deterministic core tests and HTTP integration tests. Time is
advanced with a fake `TimeProvider`; no test uses `Thread.Sleep` or depends on
real delays.

## HTTP API

```http
POST /api/requests/{clientId}
```

The endpoint does not require a request body.

| Condition | Response |
| --- | --- |
| At least one token is available | `200 OK` with a `RateLimitResult` JSON body |
| The bucket has no complete token | `429 Too Many Requests` with a result body and `Retry-After` header |
| `clientId` contains only whitespace | `400 Bad Request` with a Problem Details body |
| The `{clientId}` path segment is missing | `404 Not Found` |

`remainingTokens` reports only complete tokens. On a rejection, the JSON body
contains the precise `TimeSpan` until one token is available. The HTTP
`Retry-After` header is rounded up to whole seconds and is never less than one
second.

### Allowed request

For a client that has not used any tokens:

```shell
curl -i -X POST http://localhost:5000/api/requests/client-a
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"isAllowed":true,"remainingTokens":4,"retryAfter":null}
```

When using Windows PowerShell, run `curl.exe` if `curl` is mapped to
`Invoke-WebRequest`.

### Rate-limited request

Repeat the request six times quickly for the same new client. With the default
configuration, the sixth response is similar to:

```http
HTTP/1.1 429 Too Many Requests
Content-Type: application/json; charset=utf-8
Retry-After: 12

{"isAllowed":false,"remainingTokens":0,"retryAfter":"00:00:11.9537017"}
```

The exact body value depends on the elapsed time. The header rounds that value
up to the next whole second.

### Invalid client identifier

```shell
curl -i -X POST http://localhost:5000/api/requests/%20
```

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Invalid client identifier","status":400,"detail":"Client identifier must not be empty or whitespace."}
```

## Design documentation

See [DESIGN.md](./DESIGN.md) for the algorithm, architecture, concurrency
strategy, error handling, testing approach, trade-offs, limitations, and a
possible evolution to a distributed implementation.
