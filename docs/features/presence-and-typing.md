# Presence and typing

## Presence

Who is online and when someone was last seen.

- A person is **online** while at least one of their connections is open. A person with a background tab open is online.
- The first connection announces online. When the last closes, a wait of 5 seconds (`Presence:OfflineGrace`) starts; if no connection opens in that time, offline is announced and `LastSeenAt` is stored. A page refresh therefore does not flash the person offline.
- A server that dies announces nothing, but its connections stop being renewed and expire (after `Presence:Ttl`, 90 seconds), and another instance announces them offline.
- **Only contacts are told**: people who share an active conversation with the person. Never the person themselves. A non-contact receives nothing.
- `GET /api/presence` returns one entry for every contact: `{ userId, isOnline, lastSeenAt, at }`. A contact in the 5-second wait is still `isOnline: true`, so the snapshot agrees with the events already sent. `at` is Redis's clock when the entry was written; `lastSeenAt` is null for someone never seen offline.
- `PresenceChanged(Presence)` is the push, sent when a contact comes online or goes offline.
- Clients keep a report only if its `at` is not older than the one they hold, so a late event cannot overwrite a newer one. The snapshot is loaded when the connection is ready, after every reconnect and whenever the user gains a conversation, because pushes carry only changes.
- Clients send no heartbeats: each server renews the connections it holds every 30 seconds. See [real-time design](../architecture/real-time.md) for the Redis structures.

### In the browser

A green dot on direct chats (in the list and the header), "Online" or "Last seen ..." in the header of a direct chat, and "N online" in a group.

### Limits

There is no setting to appear offline. A change fans out to every contact, which suits small and medium groups; very large groups would need a subscription per conversation. The Redis scripts assume a single Redis node.

## Typing indicators

- `StartTyping(conversationId)` and `StopTyping(conversationId)` are hub calls. Others in the conversation receive `UserTyping(conversationId, userId, displayName, isTyping)`.
- Typing is ephemeral: nothing is stored.
- A connection can only signal typing for a conversation it has joined, so a stranger cannot push "X is typing" into a private conversation.
- At most one start event per second per conversation per connection is delivered; extra calls succeed and are dropped. Stopping is never throttled.
- The display name comes from the token, never from the client.
- Clients should send `StartTyping` every 2 to 3 seconds while someone types and `StopTyping` when idle or after sending. The web client also expires a typing indicator after 6 seconds in case the stop was lost.
- `UserTyping` can carry your own user id when you type in another tab; clients ignore it.
