# A transactional outbox with a SKIP LOCKED relay

Status: Accepted

## Context

A message was saved and then pushed. A failure or crash between the commit and the push meant members never saw the message until they reloaded.

## Decision

The change and a row in `OutboxMessages` are saved in one `SaveChanges`. The request still pushes right after the commit and marks the row. A relay (a `BackgroundService` on every instance) takes rows that are older than `Outbox:ClaimAfter` (5 s) and not dispatched with one `UPDATE ... WHERE Id IN (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING ...` that also moves `NextAttemptAt`, so the date is both the lease and the retry delay (doubling, capped at 64 times the base). After 10 attempts the relay gives up on a row and leaves it with its last error; dispatched rows and given-up rows are both removed once they are a day old. A row holds ids, not the payload: the relay builds the push from stored data, so it never sends old text after an edit or keeps signed links in a table.

## Alternatives considered

- Pushing first and saving after.
- A distributed transaction with Redis.
- Change data capture reading the Postgres log.
- `LISTEN/NOTIFY` alone: a notification while nobody listens is lost.
- `SELECT ... FOR UPDATE` held during the pushes, a designated relay, or an advisory lock per pass: they hold locks across network calls or need an election.
- Acknowledgements for exactly-once delivery: a SignalR push has none.

## Consequences

- Delivery is at least once, so every client action merges by id.
- Typing, presence, the mute echo and notification read and dismissed events stay direct and may be lost.
- A poison row cannot block the rows behind it.
- Removing `SKIP LOCKED` or the lease makes two relays push a row twice; tests pin both.

## Related

- [Real-time design](../architecture/real-time.md)
