# Catch up after a reconnect by reading what the tab missed

Status: Accepted

## Context

A client that was disconnected misses pushes. The outbox delivers to connected clients, so it cannot repair a gap.

## Decision

After a reconnect the client reloads the conversation list, and each loaded conversation asks for its latest page first. If that page reaches back to the newest message held, or is the whole history, it is merged: it brings new messages and the current state of the last messages held (an edit or deletion does not make a message newer, so asking only for "after the newest" misses them). Otherwise the client reads `after=<newest id>` page by page, then looks back over the last messages held. Past 10 pages the conversation is dropped and loaded afresh. A page that does not join up with the store is never added, so a hole is never created.

## Alternatives considered

- A server query for what changed since a time, with indexes on `EditedAt` and `DeletedAt`: would also catch old deletions but needs a schema change and the client to remember when it last looked.
- Reloading the latest 50 only: silently loses anything beyond 50.

## Consequences

- A short absence costs one request per conversation.
- The deletion of a message older than the last 50 held, in a tab away for more than a page of new messages, shows after a reload.

## Related

- [Messaging and history](../features/messaging.md)
