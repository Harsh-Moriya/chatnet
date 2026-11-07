# Page history by message id with keyset comparison

Status: Accepted

## Context

History must be stable while messages arrive, cheap in a conversation of any length, and clients already hold message ids.

## Decision

`GET .../messages` returns the latest 50, oldest first. `before=<id>` returns the 50 just older, `after=<id>` the 50 just newer and `around=<id>` up to 50 either side. The server turns the id into the message's `(CreatedAt, Id)` position and compares positions, so two messages with one timestamp are never skipped or repeated. A cursor that is not a message the caller may read is one 400, whether it is unknown, foreign or from before they joined. The response stays a bare array; a page shorter than 50 is the end.

## Alternatives considered

- An opaque cursor with a `{ messages, nextCursor }` envelope: needs encoding and tamper handling and changes the response shape.
- Offset paging: skips or repeats rows when messages arrive.
- A timestamp-only cursor: drops messages stamped in the same microsecond at a page boundary.

## Consequences

- Every history query reads about 50 rows from the `(ConversationId, CreatedAt DESC)` index however long the conversation is.
- The same cursor lookup enforces the member window.

## Related

- [Messaging and history](../features/messaging.md)
