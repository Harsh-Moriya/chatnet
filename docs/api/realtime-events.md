# Real-time events

The live connection: SignalR over WebSockets (with the library's automatic fallbacks), JSON protocol, at `/hubs/chat`.

## Connecting

- Requires a valid access token. Send it as `Authorization: Bearer` on the negotiate request and as `?access_token=` on the WebSocket (the JavaScript client does both when given an `accessTokenFactory`). A missing or invalid token is refused with a 401 before any hub code runs.
- On every connect and reconnect the server puts the connection into the group of each conversation the person is an active member of.
- The server closes the connection when the access token expires. Reconnect with a renewed token.
- Register every handler before starting the connection. Handlers must return nothing (use a block body): the JavaScript client treats a returned value as a reply to the server and logs an error.

## Calls the client can make

| Method | Arguments | Effect | Refused with |
|---|---|---|---|
| `JoinConversation` | `conversationId` | Adds this connection to the conversation's group. | "You are not a member of this conversation." (the same text for an unknown conversation) |
| `StartTyping` | `conversationId` | Others in the conversation receive `UserTyping` with `isTyping: true`. At most one per second per conversation per connection is delivered; extra calls succeed and are dropped. | The same text, if this connection has not joined the conversation. |
| `StopTyping` | `conversationId` | Others receive `UserTyping` with `isTyping: false`. Never throttled. | Never; it does nothing if the connection has not joined. |

Messages and history are never sent or loaded through the hub: those are REST.

## Events the server pushes

Arguments are positional. The examples are real payloads as the JavaScript client received them.

| Event | Arguments | Sent to | When |
|---|---|---|---|
| `ReceiveMessage` | one `Message` | every connection in the conversation's group, the sender's own tabs included | a message was saved. Identical to the REST response of the send. |
| `MessageUpdated` | one `Message`, as it is now | the group | a message was edited or deleted |
| `UserTyping` | `conversationId`, `userId`, `displayName`, `isTyping` | others in the group | someone started or stopped typing. May carry your own id when you type in another tab. |
| `MessageRead` | `conversationId`, `userId`, `readAt` | the group, the reader's tabs included | a member's read position moved forward. Only when it moved. |
| `AddedToConversation` | one `Conversation`, as this recipient sees it | the people added (and the creator's other tabs) | a conversation was created that includes you, or you were added to a group. Call `JoinConversation` for it. |
| `MembersChanged` | one `{ conversationId, members, removedUserIds }` | each person who is in the group now | the people of a group changed |
| `ConversationUpdated` | one `{ id, name, avatarUrl }` | each person in the group | a group was renamed or its picture changed |
| `RemovedFromConversation` | `conversationId`, `leftAt` | every tab of the person | they left or were removed. The conversation stays, read-only. |
| `ProfileChanged` | one `UserSummary` | everyone who shares a conversation with the person, and their own tabs | someone changed their name or picture |
| `PresenceChanged` | one `Presence` | contacts | someone came online or went offline |
| `NotificationReceived` | one `Notification` | every tab of the recipient | someone mentioned you or added you to a group |
| `NotificationsRead` | `ids` | the person's tabs | notifications were marked read, here or in another tab. Only the ids that changed. |
| `NotificationDismissed` | `id` | the person's tabs | a notification was dismissed |
| `ConversationMuteChanged` | `conversationId`, `isMuted` | the person's tabs | the person muted or unmuted a conversation |

### Examples

**ReceiveMessage** (a `MessageUpdated` carries the same shape):

```json
[
  {
    "id": "01a1b535-b662-73ed-b3bb-082e84acb467",
    "conversationId": "01a1b535-b41e-761a-91ea-5cb6239f7221",
    "senderId": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
    "senderDisplayName": "Ana Example",
    "content": "Hi everyone, shall we meet on Saturday?",
    "createdAt": "2026-11-07T08:52:38.625789Z",
    "clientMessageId": "4fb3a9f7-a04b-46bf-9b70-b91819c71af9",
    "attachments": [],
    "editedAt": null,
    "editCount": 0,
    "isDeleted": false
  }
]
```

**UserTyping**:

```json
[
  "01a1b535-b41e-761a-91ea-5cb6239f7221",
  "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
  "Ana Example",
  true
]
```

**MessageRead** (`conversationId`, the reader's id, `readAt`):

```json
[
  "01a1b535-e6c4-7af9-af41-50487eee2244",
  "01a1b535-e6af-7aac-9f3a-0f1989a94f5b",
  "2026-11-07T08:53:00.033616Z"
]
```

**AddedToConversation**:

```json
[
  {
    "id": "01a1b535-b18e-7865-8b72-13617328f38c",
    "type": "Direct",
    "name": "Ben Example",
    "avatarUrl": null,
    "updatedAt": "2026-11-07T08:52:37.38997Z",
    "lastMessage": null,
    "unreadCount": 0,
    "otherUserId": "01a1b535-abad-77a7-a8ad-aad0ec6c1972",
    "isMuted": false,
    "leftAt": null
  }
]
```

**MembersChanged**:

```json
[
  {
    "conversationId": "01a1b535-b41e-761a-91ea-5cb6239f7221",
    "members": [
      {
        "userId": "01a1b535-ac1f-7a1e-8547-7ebc88bed2c3",
        "userName": "cleo_hlqpp",
        "displayName": "Cleo Example",
        "avatarUrl": null,
        "role": "Member",
        "lastReadAt": null
      }
    ],
    "removedUserIds": []
  }
]
```

**ConversationUpdated**:

```json
[
  {
    "id": "01a1b535-b41e-761a-91ea-5cb6239f7221",
    "name": "Weekend plans (Sunday)",
    "avatarUrl": null
  }
]
```

**RemovedFromConversation**:

```json
[
  "01a1b535-b41e-761a-91ea-5cb6239f7221",
  "2026-11-07T08:52:41.525972Z"
]
```

**ProfileChanged**:

```json
[
  {
    "id": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
    "userName": "ana_hlqpp",
    "displayName": "Ana Example",
    "avatarUrl": "/api/avatars/users/01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c/01a1b535-be5b-7895-8473-2c9d9a2939f6"
  }
]
```

**PresenceChanged**, first online with no last-seen time, then offline after the person's last connection had been gone 5 seconds, then online again:

```json
[
  {
    "userId": "01a1b535-e6af-7aac-9f3a-0f1989a94f5b",
    "isOnline": true,
    "lastSeenAt": null,
    "at": "2026-11-07T08:52:51.18Z"
  },
  {
    "userId": "01a1b535-e6af-7aac-9f3a-0f1989a94f5b",
    "isOnline": false,
    "lastSeenAt": "2026-11-07T08:52:56.7Z",
    "at": "2026-11-07T08:52:56.7Z"
  },
  {
    "userId": "01a1b535-e6af-7aac-9f3a-0f1989a94f5b",
    "isOnline": true,
    "lastSeenAt": null,
    "at": "2026-11-07T08:52:59.213Z"
  }
]
```

**NotificationReceived**:

```json
[
  {
    "id": "01a1b535-b41f-7a21-9fb7-01fb06765de5",
    "type": "MemberAdded",
    "conversationId": "01a1b535-b41e-761a-91ea-5cb6239f7221",
    "actorId": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
    "actorDisplayName": "Ana Example",
    "messageId": null,
    "messagePreview": null,
    "isRead": false,
    "createdAt": "2026-11-07T08:52:38.046376Z"
  }
]
```

**NotificationsRead**, **NotificationDismissed**, **ConversationMuteChanged**:

```json
[
  [
    [
      "01a1b535-b746-7d09-8a48-5ba47a7e75a0"
    ]
  ],
  [
    "01a1b535-b746-7d09-8a48-5ba47a7e75a0"
  ],
  [
    "01a1b535-b41e-761a-91ea-5cb6239f7221",
    true
  ]
]
```

## Rules for clients

- **Merge by id.** A message can reach you twice, by push and by the REST response of your own send, and again in a later history load. If a message with that `id` is already held, do nothing. Keep the later of two copies: a deleted copy beats any other, otherwise the higher `editCount` wins.
- **Match your own sends by `clientMessageId`**, replacing the temporary entry with the confirmed message.
- **Events sent while you were disconnected are lost to you.** After reconnecting, reload the conversation list and catch up each conversation you hold over REST (see [messaging](../features/messaging.md)). Treat REST as the source of truth.
- **Load initial data only when the connection is ready**, or messages sent between the load and the group join can be missed. "Ready" is not the moment `start()` resolves, because the server may still be putting the connection into its groups. One way to know: make a harmless round trip first (the web client calls `StopTyping` with an empty GUID) and treat the answer as proof, because the server handles a connection's calls only after its connect step has finished.
- **Retry a failed first connect yourself.** The library's automatic reconnect applies only to a connection that already worked.
- **After `AddedToConversation`, call `JoinConversation`** for that id. Nothing else puts a live connection into a conversation created after it connected. If the conversation is one you already hold with `leftAt` set, you were added back: replace that copy, forget the messages and people held from before, and join.
- **`RemovedFromConversation`**: mark the conversation as left (nothing unread, no composer, no people) and keep its messages. The server has already taken your connections out of its group.
- **Presence.** Load `GET /api/presence` once the connection is ready, after every reconnect, and whenever you gain a conversation. Keep a report only if its `at` is not older than the one you hold, comparing times by value. When a report says online with `lastSeenAt: null`, keep the last-seen time you already had.
- **Read state.** `MessageRead` for your own id means another tab of yours read the conversation: lower your unread count. For anyone else it moves that member's position, never backwards. Tell the server you have read only while the page is visible, the end of the conversation is on screen and something is unread.
- **Notifications.** The notification events and `ConversationMuteChanged` patch your store directly and never cause a refetch. A read, a dismissal or a notification can arrive both as a push and as a REST answer: reading is one-way, a dismissed id is remembered, and one you already hold is not counted twice.
- **Typing.** Ignore `UserTyping` with your own id. While typing, send `StartTyping` every 2 to 3 seconds and `StopTyping` when idle or after sending; expire an indicator by yourself after a few seconds in case the stop is lost.
- **File links in pushes** are signed for the push (60 minutes). Renew a dead one once with `GET /api/attachments/{id}/url`.
- **A push can arrive late, or twice**: the outbox delivers at least once. The message list from REST is the source of truth.
