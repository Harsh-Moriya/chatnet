# Read state

Unread counts, read receipts and marking a conversation read.

## The read position

Each member has one timestamp, `ConversationMembers.LastReadAt`: they have seen every message sent at or before it.

- `POST /api/conversations/{id}/read` with `{ messageId }` means "I have read up to and including that message". The position moves to that message's time, **only forward**, through one conditional `UPDATE ... WHERE LastReadAt IS NULL OR LastReadAt < @t`. Two tabs or a retry racing each other cannot move it back and need no lock. A repeat or an older message is a 204 that changes nothing.
- The position comes from the time of the message that was read, never from the clock.
- Sending a message moves the sender's own position to that message in the same transaction.
- The message must be one the caller may read (in this conversation, sent since they joined); anything else is one 400.
- Two messages stamped in the same microsecond are read together.

## Unread counts

`unreadCount` on each conversation is the number of messages from other people, not deleted, newer than the caller's position. It is computed by a subquery in the conversation list query and stops counting at **100**, so a list load reads about 100 rows per conversation however far behind a member is (the UI shows "99+").

## Receipts

When a position moves, `MessageRead(conversationId, userId, readAt)` is pushed to every connection in the conversation's group, including the reader's other tabs, only after the new position is stored and only when it moved. The UI shows "Seen" in a direct chat and "Seen by N" in a group, from `GET /api/conversations/{id}/members`, which returns each member's read position.

A `MessageRead` for your own user id means another tab of yours read the conversation, so the unread count drops; for anyone else it moves that member's position.

There is no setting to hide your read position, and a group shows who has read to all its members.

## Mentions

Reading a conversation up to a message also marks the reader's unread mentions in it up to that message as read.

## In the browser

A tab tells the server it has read only while the page is visible, the end of the conversation is on screen and something is unread. While scrolled up with unread messages below, an "N new messages" button appears. The sidebar shows a badge per conversation.

## Endpoints and events

`POST /api/conversations/{id}/read`, `GET /api/conversations/{id}/members`; hub event `MessageRead`. See [endpoints](../api/endpoints.md).

## Limits

Marking read requires a visible page, not a focused window.
