# Editing and deleting messages

## Editing

`PATCH /api/conversations/{id}/messages/{messageId}` with `{ content }`.

- Only the **sender** can edit, and only within `Messaging:EditWindow` (15 minutes) of the message being sent. An admin cannot edit someone else's words, only remove them.
- A deleted message cannot be edited (409). An edit after the window closed is a 409.
- The new text follows the rules of sending: trimmed, at most 4,000 code units, and blank only on a message that has files.
- The same text again is a 200 that changes and pushes nothing.
- An edit increments `editCount` and sets `editedAt`. The message shows an "edited" label.
- **New `@mentions`** in the edited text are notified; people already notified by the earlier text are not notified again.
- An edit never moves the conversation in the sidebar.

The change is one conditional `UPDATE` that checks the window and the not-deleted state and counts the edit, so an edit racing a delete is settled by Postgres: a delete that arrives first wins.

## Deleting

`DELETE /api/conversations/{id}/messages/{messageId}`.

- The **sender** can delete at any time. In a group, the **owner or an admin** can delete anyone's message. In a direct chat both people are plain members, so only the sender can.
- The row stays: `content` becomes null, `isDeleted` is true, and `deletedById` records who did it. The message's files are removed from the bucket after the commit, through an outbox job that the relay repeats until the bucket accepts it, so a storage outage cannot leave a file behind.
- Deleting again answers 200 and does nothing else. A deleted message does not count as unread.
- A delete cannot be undone: the text and the files are gone.

## Visibility

A message sent before the caller joined is a 404 for any edit or delete, even to an admin, so the answer reveals nothing.

## Live update

Both changes push `MessageUpdated(Message)` through the outbox to every connection in the conversation's group, including the changer's own tabs. The pushed copy is built from stored data when it is sent, so a late push of an edited and then deleted message carries none of its text.

The browser keeps the later of two copies of one message: a deleted copy beats any other, otherwise the higher `editCount` wins. This holds for pushes, REST answers, loaded pages and repeated pushes, so a late copy cannot bring back text that was edited away or a message that was deleted. The sidebar preview, the unread count and the notification bell follow a change to a message of a conversation the tab never opened.

## In the browser

A message menu offers Edit and Delete where the person may use them (always visible on a touch pointer). Editing is inline, with Save and Cancel; deleting asks for confirmation. A group owner's menu on someone else's message offers only Delete.

## Limits

- A tab that was offline learns of edits and deletions on reconnect through the catch-up, within the last page of messages it holds. See [messaging](messaging.md).
- Edits are limited to 20 per minute per person.
