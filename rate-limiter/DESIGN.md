# Rate Limiter Design

## Goals and scope

The central component is a reusable Token Bucket rate limiter in
`RateLimiter.Core`. It has no dependency on ASP.NET Core and can be exercised
directly in unit tests or used by another adapter. `RateLimiter.Api` is only a
minimal executable HTTP adapter that demonstrates the component.

The prototype intentionally provides:

- one independent bucket per client identifier;
- a configurable burst capacity and sustained replenishment rate;
- safe concurrent access within one process;
- deterministic tests through a time abstraction; and
- explicit HTTP responses for allowed, rejected, and invalid requests.

It intentionally does not include a graphical interface, authentication,
external storage, distributed coordination, or unrelated application logic.
Rate limiting controls request frequency over time; it is different from
limiting the number of operations executing concurrently.

## Why Token Bucket

Token Bucket supports two useful properties with a small amount of state:

1. A client can make a controlled burst of up to `Capacity` requests when its
   bucket is full.
2. Over time, the client is constrained by the configured replenishment rate.

The expected work per request is constant, and only one bucket state is needed
per known client. Compared with Fixed Window, Token Bucket does not introduce a
window-boundary burst where two full quotas can be consumed almost back to
back. Compared with keeping every request timestamp for a Sliding Window, it
uses less state and has a simpler transition.

`TokensPerPeriod` is treated as a continuous rate, not as a batch deposited at
the end of each period. For example, five tokens per minute generate enough
credit for one token every 12 seconds. Replenishment is calculated lazily when
a request accesses a bucket, so no timer or background task is required.

## Architecture

```text
HTTP client
    |
    v
RateLimiter.Api
  route + HTTP validation + response mapping
    |
    v
IRateLimiter
    |
    v
TokenBucketRateLimiter
  ConcurrentDictionary<string, BucketState>
  per-bucket lock
  TimeProvider
```

Project dependencies are deliberately one-directional:

```text
RateLimiter.Api   ---> RateLimiter.Core
RateLimiter.Tests ---> RateLimiter.Core + RateLimiter.Api
```

### Responsibilities

| Component | Responsibility |
| --- | --- |
| `IRateLimiter` | Defines the minimal synchronous `TryAcquire` contract |
| `RateLimitOptions` | Holds capacity and replenishment-rate configuration |
| `RateLimitResult` | Returns the decision, number of whole tokens remaining, and optional retry time |
| `TokenBucketRateLimiter` | Owns bucket state and performs refill, consume, reject, and synchronization transitions |
| `RateLimiter.Api` | Loads configuration, registers the singleton limiter, validates HTTP input, and maps results to HTTP responses |
| `RateLimiter.Tests` | Verifies the core algorithm and the HTTP adapter |

The limiter is registered as a singleton because all requests handled by one
API process must observe the same in-memory buckets.

### HTTP request flow

For `POST /api/requests/{clientId}`:

1. Routing binds the client identifier from the URL.
2. The adapter rejects a whitespace-only identifier with `400 Bad Request`.
3. The singleton `IRateLimiter` obtains or creates that client's bucket.
4. Inside the bucket lock, elapsed time is applied and the request is accepted
   or rejected.
5. An accepted result becomes `200 OK`.
6. A rejected result becomes `429 Too Many Requests` with a `Retry-After`
   header.

## Token Bucket state and transitions

### State per client

Each bucket stores:

- available credit, including any fraction of a token;
- the monotonic timestamp of its last update; and
- a private synchronization object.

A new bucket begins full. This permits the initial burst expected from Token
Bucket and avoids penalizing a client simply because it has no prior state.

### Fixed-point credit representation

The implementation represents tokens as credit derived from `TimeSpan` ticks:

```text
creditUnitsPerToken = ReplenishmentPeriod.Ticks
capacityCredit      = Capacity * creditUnitsPerToken
replenishedCredit   = elapsedTicks * TokensPerPeriod
```

After one complete replenishment period,
`replenishedCredit` therefore represents exactly `TokensPerPeriod` tokens. The
scaled credit is retained between requests instead of being rounded down to
whole tokens. `decimal` provides sufficient range and exact arithmetic for
these credit values.

This representation is somewhat less immediately familiar than storing a
floating-point token count, but it keeps the rate and retry calculation
deterministic and avoids floating-point drift. It does not claim to eliminate
every possible source of real-world clock or scheduling imprecision.

### Request transition

Within the client's lock, `TryAcquire` performs one atomic state transition:

1. Read the current monotonic timestamp.
2. Calculate elapsed time since the last update.
3. Add `elapsedTicks * TokensPerPeriod` credit.
4. Cap the result at the configured capacity.
5. If at least one token of credit exists, subtract one token and allow the
   request.
6. Otherwise, keep the partial credit, reject the request, and calculate the
   time needed to complete one token.

The retry calculation is:

```text
missingCredit  = creditUnitsPerToken - availableCredit
retryAfterTicks = ceiling(missingCredit / TokensPerPeriod)
```

Rounding up ensures the reported time is not shorter than the time needed to
form a complete token.

The main state invariants are:

```text
0 <= availableCredit <= capacityCredit
an accepted request consumes exactly one token
a rejected request consumes no credit
```

`RemainingTokens` reports the number of complete tokens after an accepted
request. Partial credit is preserved internally but rounded down in the
response.

## Concurrency strategy

`ConcurrentDictionary<string, BucketState>` provides thread-safe lookup and
creation of client buckets. It does not, by itself, make a multi-step token
transition atomic.

Each `BucketState` therefore has its own lock. Refill, capacity capping,
availability checking, consumption, and retry calculation all occur while that
lock is held. Two simultaneous requests for the same client cannot observe and
consume the same token.

There is no global lock. Requests for different clients use different locks and
can progress independently. The critical section is synchronous, contains no
I/O or `await`, and is intentionally small. A very hot client can still create
contention on its own bucket, which is the cost of choosing a simple and clearly
correct synchronization strategy.

Under contention, `ConcurrentDictionary.GetOrAdd` may invoke its value factory
more than once, but callers use the single bucket stored for that key. Discarded
candidate objects do not alter the stored token state.

This synchronization applies only inside one limiter instance. It does not
coordinate separate processes or API instances.

## Time abstraction

The core receives a `TimeProvider` explicitly:

- the API uses `TimeProvider.System`;
- tests use `FakeTimeProvider`.

The implementation uses `GetTimestamp` and `GetElapsedTime` rather than wall
clock dates. Elapsed-time measurement is therefore not affected by normal
calendar-clock adjustments. Tests advance time explicitly and never wait for
real time to pass.

Buckets are updated only when accessed. An idle bucket consumes memory but no
CPU for replenishment.

## Error handling and HTTP decisions

Invalid state is rejected at the closest useful boundary:

| Condition | Behavior |
| --- | --- |
| Missing `RateLimit` configuration section | API startup fails explicitly |
| Null options or `TimeProvider` | Constructor throws `ArgumentNullException` |
| Non-positive capacity, token rate, or period | Constructor throws `ArgumentOutOfRangeException` |
| Null client ID in the core | `TryAcquire` throws `ArgumentNullException` |
| Empty or whitespace client ID in the core | `TryAcquire` throws `ArgumentException` |
| Whitespace-only ID received through HTTP | `400 Bad Request` with Problem Details |
| Missing `{clientId}` URL segment | `404 Not Found` because the route does not match |
| Token available | `200 OK` with `RateLimitResult` |
| Token unavailable | `429 Too Many Requests` with `RateLimitResult` and `Retry-After` |

The API handles whitespace before calling the core so invalid user input is not
turned into an unhandled server exception. No custom exception middleware was
added because the single endpoint has no additional error mapping requirement.

The `Retry-After` header uses integer delay-seconds, rounded up and with a
minimum value of one second. The JSON result retains the more precise
`TimeSpan`. Retry time is advisory: another concurrent request may consume the
next token before a waiting caller retries.

Client identifiers use `StringComparer.Ordinal`. They are case-sensitive and
are not trimmed or normalized. Identity normalization is a domain decision and
is deliberately outside this generic component.

## Testing strategy

The current suite contains 29 deterministic test cases: 23 core cases and 6
HTTP integration cases.

Core coverage includes:

- accepting requests while tokens are available;
- rejecting requests when a bucket is empty;
- full and fractional replenishment;
- retry-time calculation and rounding;
- enforcing the maximum capacity;
- independent and case-sensitive client buckets;
- invalid configuration, dependencies, and identifiers; and
- concurrent requests not consuming more available tokens than expected.

The concurrency test releases many tasks through a common start gate and checks
the total accepted count. It increases confidence in the synchronization
strategy but is not a formal proof that no race can ever exist.

HTTP integration coverage includes:

- allowed and rejected response status, JSON, and headers;
- replenishment after advancing fake time;
- independent clients through the HTTP boundary;
- whitespace input mapped to Problem Details; and
- the missing client path mapped to `404`.

HTTP tests run the real application through `WebApplicationFactory` and
`TestServer`. They replace only `IRateLimiter` with a real
`TokenBucketRateLimiter` configured with `FakeTimeProvider`. A new factory is
created per test so singleton bucket state cannot leak between tests.

`TestServer` exercises routing, dependency injection, serialization, and the
response mapping, but it does not cover real sockets or TLS. There are no load
tests, benchmarks, or distributed integration tests in this prototype.

## Trade-offs

| Decision | Benefit | Cost |
| --- | --- | --- |
| In-memory state | Fast and easy to run and test | Lost on restart and not shared across instances |
| Lazy refill | No timer, background work, or scanning of idle buckets | Inactive bucket entries remain stored |
| Lock per client | Simple atomic transition; different clients can proceed independently | Requests for one hot client are serialized briefly |
| Fixed-point `decimal` credit | Preserves fractional replenishment and avoids floating-point drift | More arithmetic and explanation than a `double` token count |
| Initially full bucket | Supports an immediate controlled burst | Some policies may prefer a gradual warm-up |
| Literal, case-sensitive IDs | No hidden identity transformations | Callers must define any required normalization |
| Fixed startup configuration | Small and predictable implementation | No hot reload or policy changes without restart |
| Minimal synchronous interface | Appropriate for local memory and easy to test | A remote state store would require an asynchronous contract |

Expected dictionary and state-transition work is O(1) per request, excluding
the cost of hashing the identifier and any lock contention. Memory is O(number
of distinct client identifiers retained), not O(1).

## Current limitations

- State is local to one process and disappears on restart or deployment.
- Multiple API instances enforce separate quotas rather than one global quota.
- Buckets have no expiration or eviction, so high-cardinality identifiers can
  cause unbounded dictionary growth.
- The API trusts the caller-provided identifier; without authentication, a
  caller can rotate identifiers to evade the limit.
- One policy applies to every client and to the single endpoint.
- Requests always cost one token; weighted operations are not supported.
- Configuration is not dynamically reloaded.
- There are no rate-limit-specific metrics, traces, dashboards, or load tests.
- Only `Retry-After` is exposed as a rate-limit header; no broader standardized
  rate-limit header set is implemented.
- Local locking provides no atomicity across processes.

These constraints are intentional for a small, explainable prototype rather
than hidden production features.

## Evolution to a distributed production design

Multiple API instances require shared state and an atomic transition. A
possible evolution would use Redis, but merely moving fields to Redis is not
sufficient: refill, capacity capping, availability checking, consumption, and
state persistence must execute as one atomic operation.

A distributed version could:

1. Store credit and the last-update timestamp in a key scoped by policy and
   client identifier.
2. Execute the complete token transition in a server-side Lua script or Redis
   function and return the decision, remaining tokens, and retry time.
3. Use a consistent time source, preferably owned by the shared store, to avoid
   disagreement between API instance clocks.
4. Apply a TTL to inactive bucket keys to control memory growth.
5. Change the core contract to an asynchronous operation with cancellation,
   because acquiring a token would now involve network I/O.
6. Add timeouts, observability, capacity planning, and an explicit fail-open or
   fail-closed policy for store failures.
7. Add authenticated identity, versioned policies, load tests, and integration
   tests against the shared implementation.

Sharding can distribute different client keys, although a single very hot
client remains a hot key. Multi-region enforcement would also require an
explicit choice between a globally consistent quota and lower-latency regional
quotas.

Redis and other distributed infrastructure are not implemented in this
prototype.

## AI usage

Generative AI was used as a development aid for planning, implementation
suggestions, test-case ideation, code review, and documentation review. Suggested
changes were evaluated one phase at a time; accepted code and documentation
were reviewed, understood, and simplified where appropriate before being kept.
The solution was validated through formatting, builds, deterministic tests, and
manual HTTP checks. Git initialization, commits, and pushes remained under
human control.

AI is not a runtime dependency of the solution.
