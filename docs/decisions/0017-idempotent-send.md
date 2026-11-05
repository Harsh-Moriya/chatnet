# A retried send is stored once, by client message id

Status: Accepted

## Context

A browser that loses the response to a send retries it, which would create a duplicate.

## Decision

A message stores the `clientMessageId` its sender sent. A partial unique index on `(ConversationId, SenderId, ClientMessageId)` where the id is not null makes a send idempotent. A send whose conversation, sender and id are already stored returns the stored message with 200 (a new message is 201 with the same body), after the membership and deactivated-account checks, and pushes it again because the first push may have failed. Two copies arriving together are settled by the index: only a unique violation (SQL state 23505) on that index counts as "already sent", the change tracker is cleared, and the winner is returned. A retry with different text gets the stored text. A send without an id is not protected.

## Alternatives considered

- A globally unique id: one user could block or probe another's ids.
- A Redis set of recent ids: forgets on restart and cannot stop a race alone.
- A separate requests table or an advisory lock per key.
- Always 201, or 409 for a repeat: 201 claims a creation that did not happen; 409 makes a correct retry look like a failure.
- `INSERT ... ON CONFLICT DO NOTHING` in raw SQL: outside EF's change tracking and the companion updates.

## Consequences

- The key is scoped to the sender and the conversation, so ids never collide across people.
- A former member is refused on a retry too.

## Related

- [Messaging and history](../features/messaging.md)
