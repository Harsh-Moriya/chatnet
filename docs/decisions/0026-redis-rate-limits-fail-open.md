# Rate limits counted in Redis, failing open

Status: Accepted

## Context

A count in each instance is N times the limit and changes with every instance added. Nothing limited a flood of sends, upload tickets, edits or searches.

## Decision

Limits are counted in Redis as a sliding window log: one sorted set per person or client address with a member for each request that went ahead, decided by one Lua script using Redis's own clock. Sign-in, registration and renewal are per address; sending, upload tickets, edits, search and push registration are per person. A policy is attached to an endpoint with `RequireRateLimiting`, and a test lists the set. The limiter runs after authentication and before authorization. While Redis cannot be reached the limits are not applied: the call has a 200 ms limit, after a failure Redis is left alone for 5 s, and one warning is logged per outage. A refused request is not counted. An earlier design counted in each instance's memory.

## Alternatives considered

- Per-instance memory.
- A fixed window: a client can double its rate across a boundary.
- A sliding-window counter or a token bucket.
- A per-conversation limit: passed by spreading over conversations.
- Failing closed: Redis's outage would stop sign-in and sending.
- A fallback to a per-instance limiter or a circuit-breaker library.

## Consequences

- The allowance is the setting however many instances run.
- While Redis is out, only the account lockout (in Postgres) protects a password.
- A Redis that loses its data restarts the counts from zero.

## Related

- [Rate limits](../features/rate-limits.md)
