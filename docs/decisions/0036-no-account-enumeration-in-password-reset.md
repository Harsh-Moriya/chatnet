# Asking for a reset link does the same work and gives the same answer for every address

Status: Accepted

## Context

A "forgot password" form that answers differently for known and unknown addresses lets anyone list who has an account. The answer is the obvious difference, but the time it takes is another: sending an email inside the request would make a real account slower.

## Decision

The endpoint answers 202 with no body for every address. The handler always does the same things: look the address up, count it against a per-address limit of emails, and save one outbox row that names the account, or nobody (an address with no account, a deactivated account, or a request over the limit). The relay, off the request, makes the token and sends the email, to the address stored on the account. The count of emails per address is kept in Redis under a keyed hash of the normalised address, so neither the address nor a hash that could be reversed is stored there. Nothing a person typed is put in the email, because the display name could carry someone else's words.

## Alternatives considered

- Answering 404 for an unknown address: simpler for the person, and it lists the accounts.
- Sending the email inside the request: the time to answer then differs for a real account, and a slow mail server makes the request slow or fail.
- Saving a row only for a real account: the insert is a small, measurable difference.
- A plain SHA-256 of the address as the Redis key: a list of addresses can be tried against it.

## Consequences

- A person who mistypes their address is told nothing; the page says an email is on its way if there is an account.
- Someone can use up the three emails an hour of an address they do not own, which delays that person's own request.
- When Redis cannot be reached the per-address count is skipped, like every other limit.
- A request that waits more than ten minutes for the relay is dropped, and a mail server that is down is retried until then.

## Related

- [Account recovery](../features/account-recovery.md)
- [A transactional outbox with a SKIP LOCKED relay](0014-transactional-outbox.md)
- [Rate limits counted in Redis, failing open](0026-redis-rate-limits-fail-open.md)
