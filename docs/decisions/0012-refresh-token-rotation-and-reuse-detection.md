# Refresh tokens rotate in families and reuse is detected

Status: Accepted

## Context

Without rotation, a copied refresh token works silently for two weeks. Several tabs may also refresh at once.

## Decision

Signing in starts a family; each refresh retires the presented token and issues the next in the same family. The retirement is one conditional `UPDATE ... WHERE RevokedAt IS NULL` in the same transaction as inserting the successor, so of simultaneous requests exactly one wins; the losers get a 409 and retry, with the cookie left alone. A retired token presented again within 30 seconds is a tab that lost a race; presented later, a copy exists, and every token of the family is revoked. The browser also serialises renewals across tabs with a Web Lock.

## Alternatives considered

- No rotation.
- Revoking every session of the user on reuse: too blunt for a lost race.
- A 401 for the race loser: it would sign out a person whose session is fine, because the winner's reply carries the new cookie.
- Read, check, write: lets several requests through.

## Consequences

- A thief and the owner both end up signed out, and nothing says which was which.
- A refresh whose reply is lost looks like theft once the grace period passes, and the session ends.

## Related

- [Accounts and sessions](../features/accounts-and-sessions.md)
