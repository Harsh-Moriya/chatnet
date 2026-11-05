# The read position is a timestamp moved forward by one UPDATE

Status: Accepted

## Context

Two tabs, a retry and a late request can race, and the position must never go backwards. Reading must never claim more than was seen.

## Decision

A member's read position is `LastReadAt`, the time of the last message they read, set from that message's `CreatedAt`, never the clock. It moves by `UPDATE ... SET LastReadAt = @t WHERE ... AND (LastReadAt IS NULL OR LastReadAt < @t)`, and the result says whether it moved; only then is anyone told. Sending moves the sender's position in the same transaction as the insert. The unread count is computed by the list query, not stored, and stops after 100 rows.

## Alternatives considered

- A "read up to now" call: claims messages that arrived since the page was drawn.
- Read then write in application code: a lost update between two tabs.
- A row per message per reader: far more data for the same answer.

## Consequences

- The database settles races without a lock.
- Two messages stamped in the same microsecond are read together.
- The sidebar badge shows "99+" past 100 and reads about 100 rows per conversation however far behind a member is.

## Related

- [Read state](../features/read-state.md)
