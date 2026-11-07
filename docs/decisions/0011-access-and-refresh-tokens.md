# A short access token and a rotating refresh token

Status: Accepted

## Context

A signed JWT cannot be taken back, so its lifetime is how long a stolen or deactivated user's token stays useful. A long lifetime is convenient and unsafe.

## Decision

The access token is a JWT (HS256) that lives 15 minutes and is kept in browser memory only. The refresh token is 32 random bytes, stored only as a SHA-256 hash, delivered in an `HttpOnly`, `Secure`, `SameSite=Strict` cookie with `Path=/api/auth`. `POST /api/auth/refresh` exchanges it for a new pair, and sign-out revokes the session. Refresh and sign-out also require `X-Requested-With: ChatNET` as a second layer behind `SameSite=Strict`. A deactivated user is not renewed and their family is revoked. An earlier design (a 7-day token in `localStorage`) was replaced.

## Alternatives considered

- A long-lived JWT: cannot be revoked.
- Server-side sessions looked up on every request: a database read per call and a different design from the stateless JWT the hub and REST share.
- A JWT as the refresh token: cannot be revoked or tracked as used.
- bcrypt or Argon2 for the token hash: 256 random bits cannot be guessed, so a fast hash is enough and a slow one would only cost every refresh.

## Consequences

- A stolen access token is worth at most 15 minutes.
- A script injected into the page can use the access token while the page is open but cannot copy the long-lived credential.
- One person per browser host: the cookie is per host.

## Related

- [Accounts and sessions](../features/accounts-and-sessions.md)
- [Security model](../architecture/security-model.md)
