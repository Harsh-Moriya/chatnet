# The gallery lists messages by a mark on the message and a partial index

Status: Accepted. Builds on [0009](0009-keyset-pagination-by-message-id.md) (the cursor is a message id) and [0032](0032-raw-sql-and-query-cost-tests.md) (a query's cost is tested).

## Context

A gallery lists every picture, video and PDF of a conversation, newest first, however long its history is. The server knows which messages have files (rows in `MessageAttachments`), but nothing indexes "the messages of this conversation that have files". A filter on the existence of an attachment walks the conversation's messages from the newest until it has found a page, which in a long conversation where files are rare is every message of it. What a file is (a picture, a video, a PDF) is inside the sealed message, so the server cannot list by kind, and the client filters what it can show.

## Decision

**A message carries a mark, `HasFiles`, set once where its files are attached.** Files are attached in one place (when a message is sent) and never change afterwards: an edit keeps them and a delete marks the message deleted. A partial index on `(ConversationId, CreatedAt DESC, Id DESC)` over the messages that are marked and not deleted serves the list, and the query repeats that filter exactly. The migration marks the messages that already have files and drops the column's default, so a message stored later has to say.

**The list is `GET /api/conversations/{id}/attachments?before=<messageId>`.** It returns the messages themselves (the same shape as the history, with the same signed links), newest first, 50 to a page, under the same member window and the same cursor rules as the history: the cursor is the id of the oldest message the caller has from the list, any message the caller may read is a valid one, and anything else is the one 400. A page shorter than 50 is the last. It is newest first, where the history is oldest first, because a gallery is read from the newest.

## Alternatives considered

- Filtering with `EXISTS` on `MessageAttachments` and the existing `(ConversationId, CreatedAt DESC)` index: no schema change, but the cost of a page depends on how rare files are, and in the case that matters (a long chat with few files) it reads every message.
- Listing from `MessageAttachments`: it has no conversation, so a page would need a join the planner cannot start from the conversation.
- A separate table of files per conversation: more to keep in step with the messages for nothing the mark does not do.
- Listing from the browser's local store of decrypted messages: it holds only what this device has opened, and a mobile client could not rely on it. The list is on the server so that every client can use it.

## Consequences

- A path that attaches files to a message without setting the mark would hide them from the gallery. There is one such path, and a test sends a message with files through the real endpoint and finds it in the list.
- A page may hold no file the client can show (other types, SVG, files the sealed message does not vouch for), so the client keeps reading pages until it has something to show or the history ends.
- The partial index is small (only the messages with files) and is maintained on send and on delete.
