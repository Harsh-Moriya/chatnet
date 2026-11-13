# Search runs in the browser over a local store kept current by a changes feed

Status: Accepted

## Context

Once messages are sealed, the server has nothing it can index. Search has to run where the text is readable, which is the browser, over messages the browser has opened. That needs a copy of the messages on the device that is complete (not only what the person happened to scroll past) and current (an edit, a delete or a message sent while the tab was closed must reach it). It also needs a way to ask the server what changed, one that a phone app can use for its own store.

## Decision

**A feed of changes per conversation.** Every conversation has a counter, `ChangeSeq`, and every message carries the number of its latest change. A send, an edit and a delete each take the next number inside the transaction that makes the change. `GET /api/conversations/{id}/changes?since=N` returns the messages with a number above `N`, in order, at most 100, with a `cursor` to continue from and `more`. A deleted message arrives as a marker. The member window of the history applies.

- The counter is bumped by an `UPDATE ... RETURNING` on the conversation row, which holds the row lock until commit. Numbers are therefore consecutive and in commit order: no transaction commits a lower number after a higher one is visible, so a client that continues from the highest number it saw misses nothing.
- The feed reads the counter first and then the changes up to it, so the cursor it returns is never above a number that is still uncommitted.
- A change that changes nothing (a stale edit, a repeated delete, a send that loses to the idempotency index, a refused epoch) ends without committing, so its number is rolled back and no gap is left. A unique index on `(ConversationId, ChangeSeq)` makes a path that forgets to take a number fail loudly.
- A point above the counter is a 409 `changes-cursor-ahead`: the server is older than the client (a restored backup), and the client rebuilds that conversation.

**A local store in IndexedDB.** The browser keeps each opened message as text, sender, time, edit count, file names and number, with the words already split, for each person on the device. It reads a conversation's history once, newest first, noting the conversation's number first, and then follows the feed from that number. Pushes are written as they are opened. A copy never replaces one with a higher number, and a deleted marker is kept so a late copy cannot bring a message back. Signing out deletes it.

**A scan, not an index.** A search walks the store newest first, tests each message against the parsed query and stops when it has a page. The syntax is every word in any order and case, the last word typed matching from its start, `"phrase"`, `-word` and `or`.

## Alternatives considered

- A Postgres sequence for the numbers: numbers are drawn before commit, so a reader can see 12 and later miss a 11 that commits after it.
- One feed per account: it needs a global order, and it does not match the member windows, which are per conversation.
- A trigger that numbers changes: the rule would be hidden from a reader and would still need the edit's lock changed, because two edits that hold a shared lock and both upgrade it deadlock.
- An inverted index in the browser: about ten extra writes per message, more storage, an index to keep in step with every edit and delete, and more code, for a store that a scan reads in time that grows with its size and not with the number of searches.
- Sealing every record in the store with a key that cannot be extracted: it protects only a copy of the files taken off the disk, because a script in the page can use the key too, and it makes every scan open every record.
- Searching only what a conversation view has loaded: it finds nothing from before the tab was opened.

## Consequences

- The text of every message the device has read is plain in IndexedDB, in the same database as the keys. Anyone who can read the browser profile, or a script in the page, can read it. Signing out removes it; a session that only expires keeps it.
- Search covers what the device has read. A new device fills its store as it reads, and says so while it does.
- Edits to a conversation serialise on its row. They are rare and short.
- A scan for a word that is rare reads the whole store, so its cost grows with the size of the store.
- The store can be rebuilt from the server at any time, so losing it costs a download and never data.
- The feed and the numbers are the contract a mobile app uses for its own store and its own index.

## Related

- [Searching messages](../features/message-search.md)
- [Messaging and history](../features/messaging.md)
- [Security model](../architecture/security-model.md)
- [Messages are sealed in the browser, and the server stores a blob it checks only for shape and epoch](0038-messages-stored-as-ciphertext.md)
- [Full-text search inside Postgres, in two phases](0022-postgres-full-text-search.md)
- [Edit and delete are single conditional UPDATEs](0019-edit-and-delete-as-conditional-updates.md)
