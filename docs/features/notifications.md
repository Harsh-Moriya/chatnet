# Notifications and mentions

A bell with an unread count and a panel, `@mentions`, invitations to groups, browser alerts, the unread count in the tab title, and muting.

## What creates a notification

| Event | Notification type | Recipient |
|---|---|---|
| A message names a member | `Mention` | that member |
| Someone is added to a group, or a group is created with them in it | `MemberAdded` | the person added (not the creator) |

New messages are not notification rows; they are counted as unread (see [read state](read-state.md)).

## Mentions

- The server cannot read the text, so the sender's browser finds the mentions and sends the people as ids (`mentionedUserIds`) next to the sealed message. A mention is `@` followed by a handle (3 to 50 letters, digits or underscores) with no word character on either side, so `ana@example.com` and a longer word are not mentions, and a name ends at a full stop or comma. Only the names of members of the conversation count.
- The server keeps only the ids of **active members of that conversation** other than the sender, and drops the rest without saying which, so a message cannot notify an outsider or reveal who exists elsewhere.
- At most 20 ids are accepted with one message; more is a 400 with nothing stored.
- The ids that counted are kept on the message, so an edit notifies only the people it names who the message did not name before.
- The notification rows are saved in the same `SaveChanges` as the message or the group, so there is never a notification about a message that does not exist, and a repeated send cannot notify twice. Each is pushed to all of the person's tabs after the commit (`NotificationReceived`, through the outbox).
- A mention still alerts in a muted conversation.

## The list

- `GET /api/notifications?before=` returns `{ items, unreadCount }`: at most 30 notifications, newest first, read and unread together. A mention carries the message it is about as `message`, sealed (`ciphertext`, `keyEpoch`, `clientMessageId`, `editCount`) and null once the message is deleted; the browser opens it to show the start of what was said, and shows no preview when it cannot. `unreadCount` stops counting at 100 (shown as "99+"). `before` is the id of one of the caller's notifications.
- The list is built from two ordered index walks (unread and read) merged, so it reads at most two pages whatever the total.
- `POST /api/notifications/read` with `{ ids? }` marks the named ids (at most 100) read, or all of the caller's when `ids` is omitted. Someone else's ids are ignored. Tabs are told which ids changed (`NotificationsRead`); nothing is sent for a repeat.
- `DELETE /api/notifications/{id}` removes one for good (`NotificationDismissed` to the caller's tabs). Another person's id gets the same 404 as an unknown one.
- Reading a conversation up to a message also reads the mentions it contains.
- Every query and update is scoped by the caller's id in the same statement.

## Mute

`PUT /api/conversations/{id}/mute` with `{ isMuted }` is the member's own choice, stored as `ConversationMembers.IsMuted` by one `UPDATE` that is also the membership check. Nobody else sees it, and the caller's other tabs are told by `ConversationMuteChanged`.

A muted conversation keeps counting what is unread, but the browser raises no alert for its new messages and its count stays out of the tab title. A mention still alerts.

## Browser alerts and the tab title

- While the page is open and connected, it shows an alert itself (the Notifications API) for a new message in a conversation that is not muted, or for a mention. The text of a message is shown in it, opened in the browser; a message that could not be opened says only that there is a new one. The rules live in one pure module: a mention beats mute, and one alert tag is used per conversation so a new alert replaces the one before.
- The tab title shows `(N) ChatNET`, where N counts unread messages of conversations that are not muted.
- For a person with **no live connection**, the server sends the same alerts as Web Push, with no text of the message in them. See [Web Push](web-push.md).

## Events

`NotificationReceived`, `NotificationsRead`, `NotificationDismissed`, `ConversationMuteChanged`. These patch the store directly and never cause a refetch. The client loads the list when the connection is ready, after every reconnect, when the panel opens and when the window regains focus.

## Limits

- There is no retention for notifications: only dismissal removes them.
- A notification older than the loaded pages that is read or dismissed in another tab leaves the badge high until the next reload.
