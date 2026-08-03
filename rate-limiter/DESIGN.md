# Rate Limiter Design

`RateLimiter.Core` is the reusable component. It owns the Token Bucket
algorithm and has no dependency on ASP.NET Core. `RateLimiter.Api` is a minimal
HTTP adapter used to configure, run, and demonstrate that component.

The prototype keeps state in memory within one process. It intentionally omits
authentication, external storage, distributed coordination, and unrelated
application infrastructure. Rate limiting controls request frequency over
time; it does not limit the number of operations executing concurrently.

## Why Token Bucket

Token Bucket supports a controlled burst of up to `Capacity` requests while
enforcing a sustained replenishment rate. It requires a small, fixed amount of
state per client and avoids the boundary spike of a Fixed Window without
storing individual request timestamps as a Sliding Window log would.

`TokensPerPeriod` is interpreted as a continuous rate. Five tokens per minute,
for example, generate enough credit for one token every 12 seconds. Refill is
calculated when a request arrives, so no timer or background task is required.

## Architecture and request flow

```text
HTTP client
    |
    v
RateLimiter.Api        validation and HTTP response mapping
    |
    v
IRateLimiter
    |
    v
TokenBucketRateLimiter in-memory state, refill, and synchronization
```

The core contract is deliberately small:

- `IRateLimiter.TryAcquire` requests one token for a client.
- `RateLimitOptions` defines capacity and replenishment rate.
- `RateLimitResult` reports the decision, whole remaining tokens, and optional
  retry time.
- `TokenBucketRateLimiter` owns all bucket state and transitions.

The API loads one policy at startup and registers the limiter as a singleton,
ensuring every request in the process observes the same buckets.

For `POST /api/requests/{clientId}`:

1. The API validates the path identifier.
2. `IRateLimiter.TryAcquire` obtains the client's bucket and performs one
   atomic transition.
3. An accepted result is mapped to `200 OK`.
4. A rejected result is mapped to `429 Too Many Requests` with `Retry-After`.

## Algorithm and state

Each client bucket stores available credit, a monotonic update timestamp, and a
private lock. A new bucket starts full, allowing the initial burst expected
from Token Bucket.

Tokens are represented as scaled credit:

```text
creditPerToken = ReplenishmentPeriod.Ticks
capacityCredit = Capacity * creditPerToken
creditAdded    = elapsed.Ticks * TokensPerPeriod
```

This retains partial-token progress between requests without floating-point
drift. On each acquisition, the limiter:

1. Calculates elapsed time and adds the corresponding credit.
2. Caps available credit at `capacityCredit`.
3. Consumes `creditPerToken` and accepts when a complete token exists.
4. Otherwise preserves the partial credit and rejects the request.

For a rejection, the wait for one complete token is:

```text
retryAfterTicks = ceiling(
    (creditPerToken - availableCredit) / TokensPerPeriod)
```

An accepted request consumes exactly one token. A rejected request consumes no
credit. `RemainingTokens` exposes only whole tokens; partial credit remains
internal for future requests.

## Concurrency and time

Buckets are stored in a `ConcurrentDictionary` for thread-safe lookup and
creation. A per-bucket lock makes refill, availability checking, consumption,
and retry calculation atomic for one client. Requests for the same client are
briefly serialized, while different clients use different locks and can
proceed independently. This guarantee is local to one limiter instance.

The core receives a `TimeProvider` and measures elapsed time with
`GetTimestamp` and `GetElapsedTime`. The API uses `TimeProvider.System`; tests
use `FakeTimeProvider`. Refill remains lazy: inactive buckets consume memory but
perform no background work.

## Error handling

Invalid state is rejected at the closest useful boundary:

| Condition | Behavior |
| --- | --- |
| Missing or invalid rate-limit configuration | API startup fails |
| Null or blank client ID in the core | Argument exception |
| Whitespace-only client ID over HTTP | `400 Bad Request` with Problem Details |
| Missing `{clientId}` path segment | `404 Not Found` |
| Token available | `200 OK` with `RateLimitResult` |
| Token unavailable | `429 Too Many Requests` with result and `Retry-After` |

The `Retry-After` header uses whole seconds rounded up, with a minimum of one
second. The JSON result retains the more precise `TimeSpan`. Client identifiers
use ordinal, case-sensitive comparison and are not normalized by the generic
component.

## Testing strategy

Core tests use `FakeTimeProvider` to verify consumption, rejection,
replenishment, capacity, retry calculation, independent clients, invalid input,
and concurrent access without real delays. The concurrency test releases many
tasks through a shared start gate and verifies the accepted count.

HTTP tests run the real application with `WebApplicationFactory`. They replace
only `IRateLimiter` with a real `TokenBucketRateLimiter` configured with fake
time, covering routing, dependency injection, serialization, status codes,
Problem Details, and `Retry-After`. Each test creates a new factory to isolate
singleton state.

The suite contains no `Thread.Sleep`. Load testing and distributed integration
testing are outside the prototype scope.

## Trade-offs

| Decision | Benefit | Cost |
| --- | --- | --- |
| In-memory state | Fast and easy to run and test | State is lost on restart and not shared |
| Lazy refill | No timers or background scanning | Inactive bucket entries remain allocated |
| Lock per client | Simple atomic transition without a global lock | One hot client's requests serialize briefly |
| Scaled `decimal` credit | Preserves fractional refill deterministically | More arithmetic than a floating-point token count |
| Synchronous interface | Appropriate for local memory | A remote store would require an asynchronous contract |

Starting buckets full is another deliberate choice: it permits a burst up to
capacity, which suits this policy but may not suit systems that require a
gradual warm-up.

## Current limitations

- State is local to one process and disappears on restart.
- Multiple API instances would enforce independent rather than global quotas.
- Buckets are never evicted, so memory grows with distinct client identifiers.
- The caller supplies the identifier; without authentication, identifiers can
  be rotated to evade the limit.
- One fixed policy applies to every client, and every request costs one token.
- There is no dynamic configuration, persistence, or rate-limit-specific
  observability.

These constraints keep the prototype small and explainable; they are not
production guarantees.

## Distributed evolution

A multi-instance deployment needs shared state and an atomic transition. Redis
could store credit and the last-update timestamp per client, but independent
reads and writes would reintroduce races. Refill, capacity capping, checking,
consumption, and persistence must run together in a server-side script or
function.

A production evolution would also require:

- a consistent time source for all API instances;
- TTL-based cleanup for inactive bucket keys;
- an asynchronous interface with cancellation and timeouts;
- an explicit fail-open or fail-closed policy for store failures; and
- metrics, load tests, and authenticated client identity.

Redis and distributed infrastructure are not implemented in this prototype.

## AI usage

Generative AI assisted with planning, implementation suggestions, test-case
ideation, review, and documentation. Accepted changes were reviewed,
understood, simplified where appropriate, and validated through builds and
tests. Git operations remained under human control.
