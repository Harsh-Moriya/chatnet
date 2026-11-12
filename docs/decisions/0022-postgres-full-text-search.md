# Full-text search inside Postgres, in two phases

Status: Superseded

## Context

Search must respect each member's window, find a message the moment it commits, and not read a conversation's whole history to find a rare word.

## Decision

One index, `IX_Messages_Search`, a GIN index on `(ConversationId, to_tsvector('simple', coalesce(Content, '')))` using `btree_gin`. The `simple` configuration means no stemming and no stop words, so a word is found as typed. The query searches each of the caller's conversations on its own in two phases: the newest 300 messages of the window read in order, and only when that read was full and found fewer than a page, the whole window through the index. The last word is a prefix; the snippet is built for the page only; the text searched for is never logged. The first version was one query through the index and read 16,666 rows to return 3, because Postgres has no statistics for a prefix and guessed badly.

## Alternatives considered

- A search engine fed from the outbox: a second system to keep in step on edits, deletions and permissions.
- The `english` configuration; a stored `tsvector` column; an index on the words alone.
- One query and a planner hint (Postgres has none), `ILIKE`, or an offset.

## Consequences

- File names are searched only among the newest 300 messages of each window.
- The deep phase relies on the planner choosing the full-text index, which holds after `VACUUM`: keep autovacuum on.
- The migration created the `btree_gin` extension.
- Superseded: the server no longer searches the text of messages, so the index, the extension and the endpoint were removed.

## Related

- [Messages are sealed in the browser, and the server stores a blob it checks only for shape and epoch](0038-messages-stored-as-ciphertext.md)
- [Conversation keys](../features/conversation-keys.md)
