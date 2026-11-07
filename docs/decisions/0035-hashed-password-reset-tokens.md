# Password reset links are random values stored hashed, not Identity tokens

Status: Accepted

## Context

A password reset needs a single-use token that expires and works whichever API instance made it. ASP.NET Core Identity can produce one, but its token providers protect the token with the data protection key ring, and every instance would have to share the same keys.

## Decision

The token is 32 random bytes in base64url, put in the URL fragment of the emailed link. Only its SHA-256 is stored, in a `PasswordResetTokens` table with the person, an expiry and a used-at time. Using a link is one conditional `UPDATE ... WHERE "UsedAt" IS NULL AND "ExpiresAt" > now RETURNING "UserId"`, in the same transaction that changes the password, so of simultaneous requests exactly one succeeds and a password that breaks a rule leaves the link usable. The same transaction locks the person, changes the stamp, revokes every refresh token, removes the push registrations and voids the other links. Identity's own reset helper is not used, because it needs the token providers.

## Alternatives considered

- Identity's token provider: no table, but it needs a key ring that every instance shares (a package and a store for the keys), and a token is single use only through the security stamp.
- A signed token (a JWT or an HMAC) with the expiry inside: nothing to store, but it cannot be made single use without storing something anyway.
- The token in the query string: it would appear in proxy logs and in the `Referer` of the next page. The fragment never leaves the browser.

## Consequences

- A copy of the table cannot be used to reset anyone's password: it holds hashes of values nobody can guess.
- The address is not confirmed when an account is created, so a reset turns whoever can read the mailbox into someone who can take the account.
- An access token issued before a reset works for up to 15 minutes more, because access tokens cannot be revoked.
- The table is one more thing to clean up: a daily job removes links that expired more than a week ago.

## Related

- [Account recovery](../features/account-recovery.md)
- [Refresh tokens rotate in families](0012-refresh-token-rotation-and-reuse-detection.md)
