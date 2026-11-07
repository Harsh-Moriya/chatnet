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
    "id": "01a1ac73-c1b3-7117-acd5-2aa6a2698db0",
    "conversationId": "01a1ac73-c01d-74ae-8845-418d508257b9",
    "senderId": "01a1ac73-bb4d-7e51-9759-fcb0091ef152",
    "senderDisplayName": "Ana Example",
    "content": "Hi everyone, shall we meet on Saturday?",
    "createdAt": "2026-11-05T16:03:49.811643Z",
    "clientMessageId": "72855c7e-d51e-44a1-9a47-267e022f2e03",
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
  "01a1ac73-c01d-74ae-8845-418d508257b9",
  "01a1ac73-bb4d-7e51-9759-fcb0091ef152",
  "Ana Example",
  true
]
```

**MessageRead** (`conversationId`, the reader's id, `readAt`):

```json
[
  "01a1ac72-251b-7f2c-9e69-f712650620d3",
  "01a1ac72-24d1-7330-96c7-f9ed4426dadf",
  "2026-11-05T16:02:13.30112Z"
]
```

**AddedToConversation**:

```json
[
  {
    "id": "01a1ac73-bf97-73fc-9a16-77a1085bc2b3",
    "type": "Direct",
    "name": "Ben Example",
    "avatarUrl": null,
    "updatedAt": "2026-11-05T16:03:49.271669Z",
    "lastMessage": null,
    "unreadCount": 0,
    "otherUserId": "01a1ac73-bbff-77e7-962c-3a6c26c96f52",
    "isMuted": false,
    "leftAt": null
  }
]
```

**MembersChanged**:

```json
[
  {
    "conversationId": "01a1ac73-c01d-74ae-8845-418d508257b9",
    "members": [
      {
        "userId": "01a1ac73-bca2-777b-9901-b3aeff395fee",
        "userName": "cleo_9111g",
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
    "id": "01a1ac73-c01d-74ae-8845-418d508257b9",
    "name": "Weekend plans (Sunday)",
    "avatarUrl": null
  }
]
```

**RemovedFromConversation**:

```json
[
  "01a1ac73-c01d-74ae-8845-418d508257b9",
  "2026-11-05T16:03:51.37398Z"
]
```

**ProfileChanged**:

```json
[
  {
    "id": "01a1ac73-bb4d-7e51-9759-fcb0091ef152",
    "userName": "ana_9111g",
    "displayName": "Ana Example",
    "avatarUrl": "/api/avatars/users/01a1ac73-bb4d-7e51-9759-fcb0091ef152/01a1ac73-c4ba-707d-a110-2a57c2e38ae1"
  }
]
```

**PresenceChanged**, first online with no last-seen time, then offline after the person's last connection had been gone 5 seconds, then online again:

```json
[
  {
    "userId": "01a1ac72-24d1-7330-96c7-f9ed4426dadf",
    "isOnline": true,
    "lastSeenAt": null,
    "at": "2026-11-05T16:02:04.464Z"
  },
  {
    "userId": "01a1ac72-24d1-7330-96c7-f9ed4426dadf",
    "isOnline": false,
    "lastSeenAt": "2026-11-05T16:02:09.952Z",
    "at": "2026-11-05T16:02:09.952Z"
  },
  {
    "userId": "01a1ac72-24d1-7330-96c7-f9ed4426dadf",
    "isOnline": true,
    "lastSeenAt": null,
    "at": "2026-11-05T16:02:12.482Z"
  }
]
```

**NotificationReceived**:

```json
[
  {
    "id": "01a1ac73-c01d-732c-8e9f-fa22e5bf1402",
    "type": "MemberAdded",
    "conversationId": "01a1ac73-c01d-74ae-8845-418d508257b9",
    "actorId": "01a1ac73-bb4d-7e51-9759-fcb0091ef152",
    "actorDisplayName": "Ana Example",
    "messageId": null,
    "messagePreview": null,
    "isRead": false,
    "createdAt": "2026-11-05T16:03:49.4055429Z"
  }
]
```

**NotificationsRead**, **NotificationDismissed**, **ConversationMuteChanged**:

```json
[
  [
    [
      "01a1ac73-c208-77a0-ad01-4043fb7cc747"
    ]
  ],
  [
    "01a1ac73-c208-77a0-ad01-4043fb7cc747"
  ],
  [
    "01a1ac73-c01d-74ae-8845-418d508257b9",
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
