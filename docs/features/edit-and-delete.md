# Editing and deleting messages

## Editing

`PATCH /api/conversations/{id}/messages/{messageId}` with `{ ciphertext, editCount, mentionedUserIds }`.

- Only the **sender** can edit, and only within `Messaging:EditWindow` (15 minutes) of the message being sent. An admin cannot edit someone else's words, only remove them.
- A deleted message cannot be edited (409). An edit after the window closed is a 409.
- The new text is sealed in the browser like the first one, under the epoch the conversation is in. The browser keeps it within 4,000 code units and trims it, as for a send. The edit carries the entries of the message's files again, so the files stay readable; a message with a file that can't be opened is not offered for editing.
- `editCount` is the count the edited message will have: one more than the count it has now. The count is part of what the sender signs, so the server cannot count for them. An edit made from an older version (another tab edited first) is a 409 with code `edit-count-stale`, and the browser reads the message again. Two edits made from the same version store one; the other gets that 409.
- The epoch rules of sending apply: an edit sealed under another epoch is a 409 `epoch-stale`, and under an epoch marked for replacement a 409 `epoch-due`.
- Every stored edit increments `editCount` and sets `editedAt`, including one that says the same thing, because a new blob is a new version. The message shows an "edited" label.
- **New names** in `mentionedUserIds` are notified. The server remembers who the latest version named, so people already notified are not notified again, a name taken out is not un-notified, and a name put back by a later edit is notified again. Only active members other than the sender count.
- An edit never moves the conversation in the sidebar.

The change is one conditional `UPDATE` that checks the window, the not-deleted state and the edit count, and writes the new blob, epoch, count and names together, so an edit racing a delete or another edit is settled by Postgres: a delete that arrives first wins. The statement is preceded by one on the conversation's row that takes the conversation's next change number and checks the epoch rules, and holds the row's lock until the commit, which keeps a removal from marking the epoch for replacement between the check and the commit. Edits and deletes of one conversation therefore take their turn, and each change is numbered for the [changes feed](message-search.md#the-changes-feed): a stale edit, a repeated delete and a refused epoch take no number.

## Deleting

`DELETE /api/conversations/{id}/messages/{messageId}`.

- The **sender** can delete at any time. In a group, the **owner or an admin** can delete anyone's message. In a direct chat both people are plain members, so only the sender can.
- The row stays: `ciphertext` becomes null, `isDeleted` is true, and `deletedById` records who did it. The sealed text is gone from the database at once. The message's files are removed from the bucket after the commit, through an outbox job that the relay repeats until the bucket accepts it, so a storage outage cannot leave a file behind.
- Deleting again answers 200 and does nothing else. A deleted message does not count as unread.
- A delete cannot be undone: the text and the files are gone.

## Visibility

A message sent before the caller joined is a 404 for any edit or delete, even to an admin, so the answer reveals nothing.

## Live update

Both changes push `MessageUpdated(Message)` through the outbox to every connection in the conversation's group, including the changer's own tabs. The pushed copy is built from stored data when it is sent, so a late push of an edited and then deleted message carries no text.

The browser keeps the later of two copies of one message: a deleted copy beats any other, otherwise the higher `editCount` wins. When two copies are the same version and only one could be opened, the text of the one that could be opened is kept. This holds for pushes, REST answers, loaded pages and repeated pushes, so a late copy cannot bring back text that was edited away or a message that was deleted. The sidebar preview, the unread count and the notification bell follow a change to a message of a conversation the tab never opened.

## In the browser

A message menu offers Edit and Delete where the person may use them (always visible on a touch pointer). Editing is inline, with Save and Cancel; deleting asks for confirmation. A group owner's menu on someone else's message offers only Delete.

## Limits

- A tab that was offline learns of edits and deletions on reconnect through the catch-up, within the last page of messages it holds. See [messaging](messaging.md).
- Edits are limited to 20 per minute per person.
