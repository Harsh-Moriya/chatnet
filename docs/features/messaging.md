# Messaging and history

The text of a message is sealed in the sender's browser before it is sent, and only the members' browsers open it. The server stores and relays a sealed blob it cannot read, and checks only its shape. The format is specified in [encryption format](../architecture/encryption-format.md); the keys it is sealed under are described in [conversation keys](conversation-keys.md).

## Sending

`POST /api/conversations/{id}/messages` with `{ clientMessageId, ciphertext, mentionedUserIds, attachments }`.

- The caller must be an active member and not deactivated.
- `ciphertext` is the sealed message as unpadded base64url: 98 to 32,768 bytes, starting with the version and suite bytes and the epoch the message was sealed under. The server checks that shape and nothing inside it. A request whose blob is not that shape is a 400 and stores nothing.
- The browser keeps the text within 4,000 UTF-16 code units and trims it before sealing; the server cannot count characters it cannot read.
- `clientMessageId` is a UUID the browser makes up. It is required, because it is bound into the sealed message: a message without one could not be opened. It is stored and returned everywhere, and it makes the send **idempotent**: a send whose conversation, sender and id are already stored is answered with the stored message, with status 200 instead of 201, and the message is pushed again (the first push may have failed). Two copies arriving together are settled by a unique index; the loser answers with the winner's message.
- `mentionedUserIds` are the people the text names, at most 20. The server keeps the ones that are active members other than the sender and notifies them; the rest are dropped without saying which. See [notifications](notifications.md).
- `attachments` is up to 4 files, each named by the key of its upload and nothing else. What each file is (name, type, size and the key that opens it) is inside the sealed message. See [attachments](attachments.md).
- A message is stored only under the epoch the conversation is in, and only while that epoch is not marked for replacement. A send sealed under another epoch is a 409 with code `epoch-stale`; a send under an epoch that a removal or a leave marked for replacement is a 409 with code `epoch-due`. In both cases nothing is stored and the browser looks at the conversation's keys again and seals once more. The check and the insert are one transaction, so a removal and a send racing each other are settled by the database: whichever commits second is refused.
- The message and the conversation's `UpdatedAt` are saved in one transaction, with an outbox row for the push. The request then pushes `ReceiveMessage` to the conversation's group, including the sender's own tabs, and marks the row. A failing push is logged and does not fail the request; the relay delivers it later.
- Sending moves the sender's read position to the new message.

## History

`GET /api/conversations/{id}/messages` returns at most 50 messages, oldest first, from when the caller joined to when they left (or to now).

| Query | Returns |
|---|---|
| none | the latest 50 |
| `before=<messageId>` | the 50 just older than that message |
| `after=<messageId>` | the 50 just newer |
| `around=<messageId>` | up to 50 older, the message, and up to 50 newer (101 at most) |

A page shorter than 50 is the end in that direction. The cursor is a message id, looked up inside the caller's own window; anything else (unknown, from another conversation, or sent before they joined) is the same 400. History is keyset-paged on `(CreatedAt, Id)`, so it is stable while new messages arrive, and every query reads about 50 rows whether the conversation holds 50 messages or 6,000.

Each message carries `ciphertext` and `keyEpoch` where a plain text would have been. A deleted message comes back with `ciphertext: null`, `isDeleted: true` and no attachments. Every message carries its attachments, each as an id, the stored size and a link signed for this response.

## Ordering

Messages are ordered by `CreatedAt`, set by the server that handled the send, then by `Id`. Messages sent one after another by one client arrive over the socket in order.

## In the browser

- **Opening.** A message that arrives, from history or by push, is opened before the rest of the app sees it: the epoch key is found (held here, or opened from the person's envelope), the sender's identity key is looked up, the signature and the sealed text are checked, and the text is read. A message that cannot be opened is not an error. It is shown as "can't be decrypted on this device" (no key for its epoch here), or as one that can't be verified (the blob does not check out, its sender has no keys, or the sender's identity key changed). The rest of the conversation is read as usual, and a message shown that way is opened again when the keys of the conversation change.
- **Order of pushes.** Opening takes a moment, so the pushes of one conversation are opened one after another in the order they arrived: an edit cannot overtake the message it is an edit of.
- **Optimistic sending.** The message appears at once as "sending" and is confirmed by whichever arrives first, the REST response or the push, matched by `clientMessageId`. A failed send shows Retry and Dismiss; retrying reuses the same id, so it cannot duplicate. The sender's own message is shown with the text that was written, not opened again.
- **Someone has no keys.** When a person in the conversation has not set up encryption, no key can be sealed for them and nothing can be sent. The box says who the conversation is waiting for and is disabled until they have set up, or are removed.
- **Scrolling back.** When the top of the list scrolls into view the page before is loaded, keeping the reader's place.
- **Windows.** A message far back (one a notification names) is shown by loading the history around it. The store then holds a window that does not reach the newest message; pushes that arrive meanwhile go into a buffer so they do not leave a hole, and join the window when it reaches the newest message. "Jump to latest" or sending drops the window and loads the latest page.
- **Catch-up after a reconnect.** Events sent while the connection was down are lost to that client, so after reconnecting each loaded conversation asks for its latest page. If that page contains the newest message the tab holds, or is short, it is merged: it brings the new messages and the current state of the last messages held, including edits and deletions. Otherwise the client reads `after=<newest id>` page by page until a short page, then looks back over the last messages held with `before=`. Past 10 pages the conversation is dropped and loaded afresh. A page that does not join up with what the store holds is never added, so a hole is never created.

## Change numbers

Every message carries `changeSeq`, the number its conversation gave to its latest change, and every conversation in the list carries the number of its latest change. `GET /api/conversations/{id}/changes?since=N` returns the messages changed after a number, which is how a client keeps a local copy current. See [searching messages](message-search.md#the-changes-feed).

## Endpoints and events

`POST` and `GET /api/conversations/{id}/messages`, `GET /api/conversations/{id}/changes`; hub events `ReceiveMessage` and `MessageUpdated`. See [endpoints](../api/endpoints.md) and [real-time events](../api/realtime-events.md).

## Limits

- The server cannot search or preview text, so it has no search endpoint; search, the sidebar preview and the notification text are done in the browser. See [searching messages](message-search.md).
- The deletion of a message older than the last 50 held, in a tab that was away for more than a page of new messages, shows after a reload.
- Sending is limited to 30 messages per 10 seconds per person.
- Files are encrypted in the browser, so the server sees how many there are and how large each is, never what they are called, what type they are or what they hold. See [attachments](attachments.md).
