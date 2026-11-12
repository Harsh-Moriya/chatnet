# Messages are sealed in the browser, and the server stores a blob it checks only for shape and epoch

Status: Accepted

## Context

If the server can read the text of a message, a leaked database, backup or log shows every conversation, and so does anyone who can read the storage. The server must still keep the conversation together: order messages, say who sent what, notify people who are named, count what is unread, and refuse a message that was sealed under a key that is no longer the right one.

## Decision

The sender's browser seals the text under the key of the conversation's current epoch and signs it with the identity key of the sender, with the conversation, the sender, the client message id and the edit count bound into both. The server stores the blob as `Ciphertext` next to the epoch in its header, and checks only its shape: the version and suite bytes, a size of 98 to 32,768 bytes, and an epoch of at least 1. It cannot open a blob or verify the signature; every reader does.

- A message is stored only under the epoch the conversation is in and while that epoch is not marked for replacement. One conditional `UPDATE` of the conversation row, in the transaction of the insert, decides it, and it takes the row lock that every change of the conversation's keys takes, so a removal and a send racing each other are settled by the database. A refusal is a `409` with `epoch-stale` or `epoch-due`, and the browser looks at the keys again and seals once more.
- An edit states the edit count the message will have, which the sender signed. The server requires exactly one more than the stored count, so an edit made from an older version is a `409` with `edit-count-stale`.
- The people a message names are sent as ids next to the blob. The server keeps the active members other than the sender, up to 20, and remembers them on the message, so an edit notifies only the people it names who the message did not.
- The text limit, the trimming and the check for a blank message move to the browser, which is the only place that can count the text.
- Pushed messages are opened in the order they arrived for each conversation. A message that cannot be opened is shown as one that cannot be, and is opened again when the keys change.
- The sidebar preview and the notification text are opened in the browser from the sealed message the list and the notification carry. Web Push alerts say who and where and never what was said.
- The server no longer searches message text.

## Alternatives considered

- Verifying the signature on the server: the platform has no Ed25519 implementation, and the readers verify anyway, so it would add a dependency for a check that protects no one.
- Taking the epoch from the request instead of the blob header: the two could disagree, and the one in the header is the one the sender signed.
- A shared advisory lock on every send to keep a removal out: it would serialize every send in a conversation. The conditional update takes the row lock only for the length of the insert.
- A plain read of the epoch before the insert: it races a removal, which could mark the epoch between the read and the commit.
- Letting the server increment the edit count: a second tab's edit would store a blob whose signed count no longer matched, which no one could open.
- Not remembering whom a message named: a person who dismissed a mention would be notified again by any later edit.

## Consequences

- The server holds no plaintext of any message, in the database, the outbox, the logs or Web Push payloads. It still sees who writes to whom, when, how large a blob is (padded to 256 bytes), how many files a message has, and the names, types and sizes of files.
- Search, previews and notification text run in the browser over what it can open. The server has no search endpoint.
- A member who is the sender can store a blob nobody can open. It is shown as a message that cannot be opened.
- A conversation that cannot get an epoch, because someone in it has not set up encryption, accepts no messages until they do or are removed.
- Existing plaintext messages were deleted when the change was applied, with the files they carried.
- Protection against a malicious server that serves changed JavaScript is out of scope: the code that seals and opens comes from the server.

## Related

- [Messaging and history](../features/messaging.md)
- [Editing and deleting messages](../features/edit-and-delete.md)
- [Conversation keys](../features/conversation-keys.md)
- [Encryption formats](../architecture/encryption-format.md)
- [Conversation keys are made by members and checked by the server only for completeness](0037-conversation-keys-made-by-members.md)
- [Edit and delete as conditional updates](0019-edit-and-delete-as-conditional-updates.md)
- [A transactional outbox with a SKIP LOCKED relay](0014-transactional-outbox.md)
