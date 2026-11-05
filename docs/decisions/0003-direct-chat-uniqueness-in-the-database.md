# Direct chats are unique by a database index

Status: Accepted

## Context

Two people can start a chat with each other at the same moment. A check-then-insert in application code lets both succeed.

## Decision

A direct conversation stores `DirectKey`, the two user ids sorted and joined (`idA:idB`). A unique partial index on `DirectKey` (where not null) and a check that a direct conversation has one make the database the final arbiter. The create path catches the unique violation, re-reads the winner and returns it, so find-or-create is safe to repeat.

## Alternatives considered

- Check then insert in application code only: loses the race.

## Consequences

- At most one direct chat per pair, however the requests interleave.
- The key is derived from the two ids and cannot be set independently.

## Related

- [Conversations](../features/conversations.md)
- [Data model](../architecture/data-model.md)
