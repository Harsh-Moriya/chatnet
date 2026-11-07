# Rate limits

Limits slow down password guessing, mass account creation and flooding. The counts live in Redis, so two API instances apply one limit between them.

## Policies

| Policy | Applies to | Counted per | Default |
|---|---|---|---|
| `auth` | `POST /api/auth/login`, `POST /api/auth/register` | client address | 10 per 60 s |
| `refresh` | `POST /api/auth/refresh`, `POST /api/auth/logout` | client address | 60 per 60 s |
| `avatars` | `GET /api/avatars/...` | client address | 600 per 60 s (misses counted) |
| `send` | sending a message | person | 30 per 10 s |
| `uploads` | upload tickets for files and pictures | person | 60 per 3,600 s |
| `edit` | editing a message | person | 20 per 60 s |
| `search` | message search | person | 30 per 10 s |
| `push` | registering and removing a browser for alerts | person | 20 per 60 s |
| `forgot-password` | `POST /api/auth/forgot-password` | client address | 5 per 900 s |
| `reset-password` | `POST /api/auth/reset-password` | client address | 10 per 900 s |
| `password-reset-email` | the emails sent for a reset link (counted by the handler, not the pipeline) | email address | 3 per 3,600 s |

The per-email count is made by the handler that asks for a link, because the address is in the request body, which the limiter in the pipeline has not read. Its key holds a keyed hash of the normalised address, never the address. Going over it gives the same `202` and no email; see [account recovery](account-recovery.md).

Not covered by these policies: reading, marking read, muting, deleting and presence. Typing is not counted in Redis; the hub delivers at most one start event per second per conversation per connection.

Configure each as `RateLimiting:<Policy>:PermitLimit` and `WindowSeconds` (for example `RateLimiting__Send__PermitLimit`). Values are validated at startup.

## Behaviour

- Over the limit the answer is a **429** `application/problem+json` with a `Retry-After` header (seconds). A refused request is not carried out and does not count.
- The limiter runs after authentication (it needs the person) and before authorization and the endpoint, so a refused request costs no password or database work.
- Behind a reverse proxy the client address is the one the trusted proxy reports (`ForwardedHeaders:KnownNetworks`). Without that setting the proxy's own address would be counted for everyone.

## How it counts

A **sliding window log**: a Redis sorted set per person or client address, with one member per request that went ahead, trimmed to the window. One Lua script decides, using Redis's own clock, so servers that act at once cannot all slip past. It is exact, gives an exact `Retry-After`, and with limits of 10 to 600 costs a few hundred bytes per active key. Keys are prefixed (`RateLimiting:Store:KeyPrefix`, default `ratelimit`) so environments that share a Redis do not count each other, and each holds a person's id or an address for at most one window.

## When Redis is down

The limits **fail open**: the call to Redis is cut off after 200 ms (`RateLimiting:Store:TimeoutMilliseconds`), the limit is then skipped for 5 seconds (`PauseSeconds`), one warning is logged per outage with the failure type only, and one line when Redis is back. A bug in the limiter code is not hidden as an outage. While Redis is out, only the account lockout (in Postgres) still protects a password. A Redis that loses its data starts the counts again from zero.

## In the browser

A refused message shows the server's text with Retry and Dismiss, and Retry works once the window passes.
