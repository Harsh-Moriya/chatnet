# Endpoints

The HTTP API. In Development the running API also serves its OpenAPI document at `/openapi/v1.json` and an interactive reference at `/scalar/v1`; every operation there declares its success body and each error it can return. The tables below list every route (58 operations) with its authentication, its rate-limit policy and what it does. Request and response bodies in the examples were copied from a running API.

## Conventions

| Topic | Convention |
|---|---|
| Base path | `/api/<resource>`. The hub is `/hubs/chat` and `/health` is outside `/api`. There is no version prefix. |
| Format | JSON in both directions (`Content-Type: application/json`). Other content types get a 415. |
| Property names | camelCase. |
| Nulls | present as `null`, not omitted. |
| Enums | strings (`"Direct"`, `"Member"`), in REST and SignalR. |
| IDs | UUID strings. Treat them as opaque. |
| Timestamps | ISO 8601 in UTC. Always `Z`. Most carry up to microseconds (`2026-11-05T16:01:09.227119Z`); the expiry of an access token, an upload ticket or a file link is whole seconds (`2026-11-07T06:27:33Z`). Parse them with a date parser and do not depend on the number of fractional digits. |
| Success | The object itself, with no envelope. A list is a bare JSON array. |
| Caller identity | Always from the token's `sub` claim. A body never says who the caller is. |
| Authentication | `Authorization: Bearer <access token>`. On `/hubs/chat` the token may also be sent as `?access_token=`. |
| Errors | Always `application/problem+json`. See [errors](errors.md). |
| Caching | API responses are `Cache-Control: no-store`, except pictures. |
| Body size | Requests over 1 MB get a 413. |

Authentication column: **Bearer** needs an access token; **Refresh cookie** needs the `chatnet_refresh` cookie and the header `X-Requested-With: ChatNET`; **None** is open. The rate-limit column names a [policy](../features/rate-limits.md).

## Health

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /health` | None |  | 200, 503 | Reports whether this instance can reach Postgres and Redis. 503 names the failing check and never says why. |

## Accounts and sessions

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `POST /api/auth/register` | None | auth | 200, 400, 409, 429 | Creates an account and signs it in. Sets the refresh cookie. |
| `POST /api/auth/login` | None | auth | 200, 400, 401, 403, 429 | Signs in. 401 for a wrong email or password (same text), 403 for a deactivated account, 429 for the rate limit or a locked account. An optional `X-Device-Id` header ties the session to a device that holds the encryption keys; see [encryption keys](../features/encryption-keys.md). |
| `POST /api/auth/refresh` | Refresh cookie | refresh | 200, 401, 403, 409, 429 | Exchanges the refresh cookie for a new access token and a new cookie; the old refresh token stops working. Needs `X-Requested-With: ChatNET`. 409 means another request replaced the token a moment ago: retry. An optional `X-Device-Id` header ties the session to a device that holds the encryption keys; see [encryption keys](../features/encryption-keys.md). |
| `POST /api/auth/logout` | Refresh cookie | refresh | 204, 403, 429 | Revokes the session and removes the cookie. A missing cookie is also a 204. Needs `X-Requested-With: ChatNET`. |
| `POST /api/auth/forgot-password` | None | forgot-password | 202, 400, 429, 503 | Asks for an email with a link to choose a new password (`email`). Answers 202 with no body for every address, so it never says whether one has an account. At most 3 emails an hour go to one address. 503 when the server has no mail relay. |
| `POST /api/auth/reset-password` | None | reset-password | 204, 400, 429 | Sets a new password from the link in that email (`token`, `newPassword`) and ends every session of the account. 204 with no body and no cookie. One 400 for an unknown, used, expired or voided link; a password that breaks a rule is a 400 on `NewPassword` and leaves the link usable. |

## Users and profile

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/users` | Bearer |  | 200, 400, 401 | Searches people by the start of their handle or display name (`query`, at least 2 characters). At most 20, never the caller. |
| `GET /api/users/me` | Bearer |  | 200, 401, 404 | The caller's own profile. |
| `PATCH /api/users/me` | Bearer |  | 200, 400, 401, 403, 404 | Changes the display name (`displayName`, trimmed, 1 to 100 characters). |
| `POST /api/users/me/avatar/uploads` | Bearer | uploads | 200, 400, 401, 403, 429 | Ticket for uploading a profile picture straight to the bucket (`contentType`, `sizeBytes`; PNG, JPEG, WebP or GIF, up to 2 MB). |
| `PUT /api/users/me/avatar` | Bearer |  | 200, 400, 401, 403, 404 | Sets the picture from an uploaded object (`uploadKey`). Answers the picture's address. |
| `DELETE /api/users/me/avatar` | Bearer |  | 200, 401, 403, 404 | Removes the picture. |
| `GET /api/avatars/users/{ownerId}/{fileId}` | None | avatars | 200, 404, 429 | The picture, cacheable for a year. Open without a token on purpose; the address cannot be guessed. A changed picture has a new address. |
| `GET /api/avatars/groups/{ownerId}/{fileId}` | None | avatars | 200, 404, 429 | A group picture, served the same way. The owner id in the address is the conversation id. |

## Conversations

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/conversations` | Bearer |  | 200, 401 | The caller's conversations, newest activity first, not paginated. Includes groups the caller left (`leftAt` set). |
| `GET /api/conversations/{conversationId}` | Bearer |  | 200, 401, 403 | One conversation as the caller sees it. 403 for a stranger and for an unknown id alike. |
| `POST /api/conversations/direct` | Bearer |  | 200, 400, 401, 404, 409 | Finds or creates the direct chat with `otherUserId`. A new chat is made with its first key epoch: `id` is the id the caller chose for it and `epoch` is the first epoch, made for the two people (see [conversation keys](../features/conversation-keys.md)). Safe to repeat: a chat that exists is returned and both are ignored. 409 with a `code` when the epoch is not for exactly the two people or the other person has no keys. |
| `POST /api/conversations/group` | Bearer |  | 201, 400, 401, 404, 409 | Creates a group (`id`, `name`, `memberIds`, `epoch`). The caller is the owner. `id` is the id the caller chose and `epoch` the first key epoch, made for the caller and every member (see [conversation keys](../features/conversation-keys.md)). 201 with a `Location` header. 409 with a `code` when the epoch is not for exactly those people, someone has no keys, or the id is in use. |
| `PATCH /api/conversations/{conversationId}` | Bearer |  | 200, 400, 401, 403, 409 | Renames a group (`name`). Owner or admin. |
| `PUT /api/conversations/{conversationId}/mute` | Bearer |  | 204, 400, 401, 403 | The caller's own mute choice (`isMuted`, required). |
| `POST /api/conversations/{conversationId}/read` | Bearer |  | 204, 400, 401, 403 | Marks the conversation read up to `messageId`. Only moves forward. |
| `POST /api/conversations/{conversationId}/avatar/uploads` | Bearer | uploads | 200, 400, 401, 403, 429 | Ticket for a group picture. Owner or admin. |
| `PUT /api/conversations/{conversationId}/avatar` | Bearer |  | 200, 400, 401, 403, 409 | Sets the group picture from an uploaded object. Owner or admin. |
| `DELETE /api/conversations/{conversationId}/avatar` | Bearer |  | 200, 401, 403, 409 | Removes the group picture. Owner or admin. |

## Members

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/conversations/{conversationId}/members` | Bearer |  | 200, 401, 403 | The active members, earliest joiner first, with role and read position. |
| `POST /api/conversations/{conversationId}/members` | Bearer |  | 200, 400, 401, 403, 404, 409 | Adds people (`userIds`, 1 to 100) together with the new key epoch (`epoch`) made for everyone who will be in the group, so those who join cannot read what came before (see [conversation keys](../features/conversation-keys.md)). Owner or admin. Answers the members after the change. 409 with a `code` when the epoch is not the next one or not for exactly those people. |
| `DELETE /api/conversations/{conversationId}/members/{userId}` | Bearer |  | 200, 401, 403, 404, 409 | Removes a member. The owner removes anyone; an admin removes plain members. The conversation then reports `keyRotationRequired` until a member stores the next epoch. |
| `PUT /api/conversations/{conversationId}/members/{userId}/role` | Bearer |  | 200, 400, 401, 403, 404, 409 | Sets a role (`Owner`, `Admin` or `Member`). Owner only; `Owner` hands the group over. |
| `POST /api/conversations/{conversationId}/leave` | Bearer |  | 200, 401, 403, 409 | Leaves a group. Answers `leftAt`. An owner who leaves hands the group to the longest-standing admin, else member. The conversation then reports `keyRotationRequired` until a member stores the next epoch. |

## Conversation keys

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/conversations/{conversationId}/key-epochs` | Bearer | keys | 200, 400, 401, 403, 429 | The epochs of the conversation the caller has an envelope for, oldest first (`from`, default 1; `limit`, default 20, at most 50), with `hasMore`. A member, or someone who was one. Each carries the caller's own envelope and never anyone else's. |
| `POST /api/conversations/{conversationId}/key-epochs` | Bearer | keys | 204, 400, 401, 403, 409, 429 | Stores the next epoch (`epoch`, `commitment`, `signature`, `envelopes`). Any member. 204. 409 with a `code`: `epoch-stale` (not the next number), `epoch-members-changed` (not for exactly the people in the conversation, or sealed to an old key generation) or `member-has-no-keys`. |

## Messages

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `POST /api/conversations/{conversationId}/messages` | Bearer | send | 200, 201, 400, 401, 403, 429 | Sends a message (`content`, `clientMessageId`, `attachments`). 201 for a new message, 200 with the stored message when this `clientMessageId` was already sent. |
| `GET /api/conversations/{conversationId}/messages` | Bearer |  | 200, 400, 401, 403 | History, at most 50, oldest first. At most one of `before`, `after`, `around` (a message id). |
| `PATCH /api/conversations/{conversationId}/messages/{messageId}` | Bearer | edit | 200, 400, 401, 403, 404, 409, 429 | Edits the caller's own message within the edit window (`content`). |
| `DELETE /api/conversations/{conversationId}/messages/{messageId}` | Bearer |  | 200, 401, 403, 404 | Deletes a message. The sender at any time; a group owner or admin for anyone's. |

## Files

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `POST /api/conversations/{conversationId}/uploads` | Bearer | uploads | 200, 400, 401, 403, 429 | Ticket for uploading one attachment straight to the bucket (`fileName`, `contentType`, `sizeBytes`). |
| `GET /api/attachments/{attachmentId}/url` | Bearer |  | 200, 401, 404 | A new signed link for a file the caller may read. One 404 for every refusal. |

## Search

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/search/messages` | Bearer | search | 200, 400, 401, 403, 429 | Searches the messages the caller may read (`q`, `conversationId`, `before`). 20 per page, newest first. |

## Notifications

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/notifications` | Bearer |  | 200, 400, 401 | The caller's notifications, 30 per page, newest first (`before` is a notification id), with `unreadCount` (stops at 100). |
| `POST /api/notifications/read` | Bearer |  | 204, 400, 401 | Marks notifications read (`ids`, at most 100), or all when `ids` is omitted. |
| `DELETE /api/notifications/{notificationId}` | Bearer |  | 204, 401, 404 | Dismisses one notification for good. |

## Presence

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/presence` | Bearer |  | 200, 401 | Whether each contact is online, and when they were last seen. |

## Web Push

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/push/key` | Bearer |  | 200, 401, 404 | The server's public key for subscribing. 404 when push is off. |
| `PUT /api/push/subscriptions` | Bearer | push | 204, 400, 401, 404, 429 | Registers or renews a browser (`endpoint`, `keys.p256dh`, `keys.auth`). 404 when push is off. |
| `DELETE /api/push/subscriptions` | Bearer | push | 204, 400, 401, 429 | Removes a browser (`endpoint`). Works when push is off. |

## Encryption keys

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/keys/me` | Bearer | keys | 200, 401, 429 | Whether the caller has set up encryption: the signed public bundle of every generation (`bundles`) and the version of the encrypted backup (`backup`, null before set-up). |
| `POST /api/keys/me` | Bearer | keys | 201, 400, 401, 409, 429 | First set-up: the bundle of generation 1 and the first backup (`bundle`, `backup`), stored together. 201 with the state. 409 when the caller already has keys. |
| `GET /api/keys/me/backup` | Bearer | keys | 200, 401, 404, 429 | The caller's encrypted backup (`blob`, `version`). Never another person's: the route takes no id. 404 before set-up. |
| `POST /api/keys/lookup` | Bearer | keys | 200, 400, 401, 429 | The current public bundle of each person named (`userIds`, 1 to 101) who has set up encryption. People without keys are left out of the answer. |
| `PUT /api/keys/me/backup` | Bearer | keys | 200, 400, 401, 404, 409, 429 | Replaces the backup (`blob`, `expectedVersion`), for a new passphrase. 409 when the backup was changed since that version, 404 before set-up. |

## Devices

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `POST /api/devices` | Bearer | keys | 201, 400, 401, 409, 429 | Adds the calling browser or phone as a device that holds the keys (`kind`: `Web`, `Ios` or `Android`; `name`, 1 to 100 characters). 409 before set-up and at the limit of devices. |
| `GET /api/devices` | Bearer | keys | 200, 401, 429 | The caller's devices that are not removed, oldest first. |
| `DELETE /api/devices/{deviceId}` | Bearer | keys | 204, 401, 404, 429 | Removes one of the caller's devices and ends the sessions that were signed in on it. One 404 for a device that is not theirs, does not exist or was removed. |
| `POST /api/devices/link-requests` | Bearer | keys | 201, 400, 401, 409, 429 | A new device asks for the keys (`temporaryPublicKey`, `code`, `kind`, `name`). 409 before set-up and when too many requests are open. |
| `GET /api/devices/link-requests` | Bearer | keys | 200, 400, 401, 404, 429 | Finds the caller's open request with this `code` (in the query). One 404 for an unknown, expired or approved request. |
| `GET /api/devices/link-requests/{requestId}` | Bearer | keys | 200, 401, 404, 429 | How the request is doing. After approval the first answer carries the sealed secrets in `payload` and clears them; the next says `Collected`. |
| `POST /api/devices/link-requests/{requestId}/approve` | Bearer | keys | 204, 400, 401, 404, 409, 429 | Sends the secrets sealed to the key of the request (`payload`). 409 when it was approved already or expired. |

## Shapes

Examples are real responses. Where a list is shown, it is cut to its first entry.

### AuthResponse

`POST /api/auth/register`, `/login` and `/refresh` answer:

```json
{
  "token": "<access token>",
  "userId": "01a1ba93-d895-7db5-9ca8-59569a0b63b7",
  "userName": "ana_fgvdc",
  "displayName": "Ana Example",
  "accessTokenExpiresAt": "2026-11-08T10:08:33Z"
}
```

The refresh token is in the `Set-Cookie` header, not the body: `chatnet_refresh=<token>; path=/api/auth; secure; samesite=strict; httponly`.

### UserSummary

`GET /api/users` (a list of these), `GET` and `PATCH /api/users/me`:

```json
{
  "id": "01a1ba93-d895-7db5-9ca8-59569a0b63b7",
  "userName": "ana_fgvdc",
  "displayName": "Ana Example",
  "avatarUrl": null
}
```

`avatarUrl` is a relative address (`/api/avatars/users/{ownerId}/{fileId}`) or null.

### Conversation

```json
{
  "id": "468f5388-4e70-47fd-b26a-36413ff08a62",
  "type": "Direct",
  "name": "Ben Example",
  "avatarUrl": null,
  "updatedAt": "2026-11-08T16:18:00.377311Z",
  "lastMessage": null,
  "unreadCount": 0,
  "otherUserId": "01a1bbf3-c874-7b04-a41c-d4600e4f3f50",
  "isMuted": false,
  "leftAt": null,
  "keyEpoch": 1,
  "keyRotationRequired": false
}
```

`keyEpoch` is the newest key epoch of the conversation, 0 while it has none, and `keyRotationRequired` says someone left since it was made. See [conversation keys](../features/conversation-keys.md).

For a direct chat `name` is the other person's display name and `otherUserId` is their id. For a group, `otherUserId` is null. `lastMessage` is null for an empty conversation, otherwise:

```json
{
  "senderId": "01a1ba93-d8f5-7d26-9b60-8314d89a1c5d",
  "content": "Sounds good 3",
  "createdAt": "2026-11-08T09:53:36.142447Z",
  "attachmentCount": 0
}
```

### Message

```json
{
  "id": "01a1ba93-e0af-7e03-a39a-697fd0f2a5c4",
  "conversationId": "01a1ba93-deb9-7a50-ac60-f3f94ff76779",
  "senderId": "01a1ba93-d895-7db5-9ca8-59569a0b63b7",
  "senderDisplayName": "Ana Example",
  "content": "Hi everyone, shall we meet on Saturday?",
  "createdAt": "2026-11-08T09:53:35.919264Z",
  "clientMessageId": "2ad938ec-75e7-4ea3-a509-156441670f82",
  "attachments": [],
  "editedAt": null,
  "editCount": 0,
  "isDeleted": false
}
```

A message with a file carries its attachments:

```json
{
  "id": "01a1ba93-e4f6-7243-ad69-5d77678d76e3",
  "type": "Image",
  "fileName": "map.png",
  "contentType": "image/png",
  "sizeBytes": 70,
  "url": "http://localhost:9000/chatnet/attachments/01a1ba93-deb9-7a50-ac60-f3f94ff76779/01a1ba93-e4f6-7243-ad69-5d77678d76e3?<signature parameters>"
}
```

A deleted message has `content: null`, `isDeleted: true` and no attachments.

### Member

`GET /api/conversations/{id}/members` and the group-changing calls answer a list of these:

```json
[
  {
    "userId": "01a1ba93-d895-7db5-9ca8-59569a0b63b7",
    "userName": "ana_fgvdc",
    "displayName": "Ana Example",
    "avatarUrl": null,
    "role": "Owner",
    "lastReadAt": "2026-11-08T09:53:36.044394Z"
  }
]
```

### Group update and leave

`PATCH /api/conversations/{id}`:

```json
{
  "id": "01a1ba93-deb9-7a50-ac60-f3f94ff76779",
  "name": "Weekend plans (Sunday)",
  "avatarUrl": null
}
```

`POST /api/conversations/{id}/leave`:

```json
{
  "leftAt": "2026-11-08T09:53:38.027205Z"
}
```

### UploadTicket

`POST .../uploads` and `POST .../avatar/uploads`:

```json
{
  "uploadKey": "pending/01a1ba93-deb9-7a50-ac60-f3f94ff76779/01a1ba93-d895-7db5-9ca8-59569a0b63b7/75b5b7dd-1cc0-4bb0-a9a3-0795242dd7ed",
  "url": "http://localhost:9000/chatnet/pending/01a1ba93-deb9-7a50-ac60-f3f94ff76779/01a1ba93-d895-7db5-9ca8-59569a0b63b7/75b5b7dd-1cc0-4bb0-a9a3-0795242dd7ed?<signature parameters>",
  "expiresAt": "2026-11-08T10:03:36Z"
}
```

`url` is a presigned PUT to the bucket, valid for 10 minutes and for exactly the declared content type.

### Attachment link

`GET /api/attachments/{id}/url`:

```json
{
  "url": "http://localhost:9000/chatnet/attachments/01a1ba93-deb9-7a50-ac60-f3f94ff76779/01a1ba93-e4f6-7243-ad69-5d77678d76e3?<signature parameters>",
  "expiresAt": "2026-11-08T10:53:37Z"
}
```

### Notifications

`GET /api/notifications`:

```json
{
  "items": [
    {
      "id": "01a1ba93-e13e-75a2-8fb8-ff6bdf90858a",
      "type": "Mention",
      "conversationId": "01a1ba93-deb9-7a50-ac60-f3f94ff76779",
      "actorId": "01a1ba93-d895-7db5-9ca8-59569a0b63b7",
      "actorDisplayName": "Ana Example",
      "messageId": "01a1ba93-e12c-7ee9-b9a4-8ac965d11952",
      "messagePreview": "@ben_fgvdc can you book a table?",
      "isRead": false,
      "createdAt": "2026-11-08T09:53:36.044394Z"
    }
  ],
  "unreadCount": 2
}
```

### Presence

`GET /api/presence`:

```json
[
  {
    "userId": "01a1ba93-d8f5-7d26-9b60-8314d89a1c5d",
    "isOnline": true,
    "lastSeenAt": null,
    "at": "2026-11-08T09:53:36.533Z"
  }
]
```

### Search hit

A hit found by its text:

```json
[
  {
    "messageId": "01a1ba93-e522-7427-a9c2-36d136cc72a8",
    "conversationId": "01a1ba93-deb9-7a50-ac60-f3f94ff76779",
    "conversationName": "Weekend plans (Sunday)",
    "conversationType": "Group",
    "senderId": "01a1ba93-d895-7db5-9ca8-59569a0b63b7",
    "senderDisplayName": "Ana Example-Jones",
    "createdAt": "2026-11-08T09:53:36.895834Z",
    "snippet": [
      {
        "text": "Meeting",
        "match": true
      },
      {
        "text": " point",
        "match": false
      }
    ],
    "fileName": null
  }
]
```

A hit found by the name of a file has `"snippet": []` and `"fileName": "map.png"`.

### Push

`GET /api/push/key`:

```json
{
  "publicKey": "BNxsDJT6Wp0Koob0q6hwBJD7XCqWCFuZR0eF-wZIKWxK4P68eLCuNFX7gOU8CRW-rwM9-E8hRsVfkZK3cZo6TWI"
}
```

`PUT /api/push/subscriptions` takes the shape of the browser's `PushSubscription.toJSON()` and answers 204:

```json
{
  "endpoint": "https://fcm.googleapis.com/fcm/send/example-fgvdc",
  "keys": {
    "p256dh": "BGuCQMIIIS79aO2_qob46p_E-ncq_JVcy-YD2mp488-ujcUljmnh06KYicC0EtBjkF7HdeTYgYBSxmsykJkObgk",
    "auth": "7442OM2nkDq9DbjRhqTxxQ"
  }
}
```

### Encryption keys

Bytes are base64url without padding. Long values are shortened here. `GET /api/keys/me` before set-up:

```json
{
  "bundles": [],
  "backup": null
}
```

`POST /api/keys/me` takes the bundle and the backup, and answers 201 with the state:

```json
{
  "bundle": "AQEAAAABaUBlLcQF5H8UWIAG...",
  "backup": "AQEBYVfw9rdeXP98nxRe66KM..."
}
```

```json
{
  "bundles": [
    {
      "generation": 1,
      "bundle": "AQEAAAABaUBlLcQF5H8UWIAG..."
    }
  ],
  "backup": {
    "version": 1,
    "updatedAt": "2026-11-08T09:52:43.958118Z"
  }
}
```

`GET /api/keys/me/backup`:

```json
{
  "blob": "AQEBYVfw9rdeXP98nxRe66KM...",
  "version": 1,
  "updatedAt": "2026-11-08T09:52:43.958118Z"
}
```

`PUT /api/keys/me/backup` takes `{ "blob": ..., "expectedVersion": 1 }` and answers:

```json
{
  "version": 2,
  "updatedAt": "2026-11-08T09:52:44.043163Z"
}
```

### Conversation keys

`POST /api/keys/lookup` takes `{ "userIds": [...] }` and answers the current bundle of each person who has keys:

```json
{
  "userIds": [
    "01a1bbf3-c874-7b04-a41c-d4600e4f3f50",
    "01a1bbf3-c910-756e-9efe-ba3ad39e09aa"
  ]
}
```

```json
{
  "bundles": [
    {
      "userId": "01a1bbf3-c874-7b04-a41c-d4600e4f3f50",
      "generation": 1,
      "bundle": "AQEAAAABtI4yfSSfHxshzXaE..."
    },
    {
      "userId": "01a1bbf3-c910-756e-9efe-ba3ad39e09aa",
      "generation": 1,
      "bundle": "AQEAAAABPRZoFGCRfSmsCVMo..."
    }
  ]
}
```

A new conversation, and a group that people are added to, carry the epoch. `POST /api/conversations/direct` takes the id the caller chose, the other person and the first epoch:

```json
{
  "id": "468f5388-4e70-47fd-b26a-36413ff08a62",
  "otherUserId": "01a1bbf3-c874-7b04-a41c-d4600e4f3f50",
  "epoch": {
    "epoch": 1,
    "commitment": "TFsh7mLDRIvoBeoTc_olqRzf3G_LJY42ZwXqTQmDm6g",
    "signature": "8vsQ8_OCjmRfZVafYB9c2pbv...",
    "envelopes": [
      {
        "userId": "01a1bbf3-c533-7794-919e-5ad1374bbee2",
        "generation": 1,
        "envelope": "AQFymvtrYgXBtAmvxmmVD2dY..."
      },
      {
        "userId": "01a1bbf3-c874-7b04-a41c-d4600e4f3f50",
        "generation": 1,
        "envelope": "AQFq76fifvjCtjqHQCuCTrt-..."
      }
    ]
  }
}
```

`POST /api/conversations/{id}/key-epochs` takes the epoch itself (the same object) and answers 204. `GET` answers a page of the epochs the caller has an envelope for:

```json
{
  "epochs": [
    {
      "epoch": 1,
      "creatorId": "01a1bbf3-c533-7794-919e-5ad1374bbee2",
      "reason": "Created",
      "createdAt": "2026-11-08T16:18:01.079767Z",
      "commitment": "xhYxtF52vCim98Wj6book6Ady8_fsTm-osJcQ2IAKrs",
      "signature": "Ho7qLaaj66XxhOBf66bDN2sb...",
      "members": [
        {
          "userId": "01a1bbf3-c533-7794-919e-5ad1374bbee2",
          "generation": 1
        },
        {
          "userId": "01a1bbf3-c874-7b04-a41c-d4600e4f3f50",
          "generation": 1
        },
        {
          "userId": "01a1bbf3-c910-756e-9efe-ba3ad39e09aa",
          "generation": 1
        }
      ],
      "generation": 1,
      "envelope": "AQEIo9BZjaMrpeL2QxVsc_e6..."
    }
  ],
  "hasMore": false
}
```

`members` is every person the epoch was made for, with the generation of the account key each envelope is sealed to; `generation` and `envelope` are the caller's own. A refused epoch is a 409 whose problem details carry a `code`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "Another epoch was stored first. Look at the conversation again.",
  "code": "epoch-stale",
  "traceId": "00-147ec60d8c0999aae38174f2a1bbcfa9-8cc550fc71f558b2-01"
}
```

### Device

`POST /api/devices` takes `{ "kind": "Web", "name": "Chrome on Windows" }` and answers 201 with the device; `GET /api/devices` answers a list of these:

```json
{
  "id": "01a1ba93-1665-7c52-9f20-392c3a44e4ad",
  "kind": "Web",
  "name": "Chrome on Windows",
  "createdAt": "2026-11-08T09:52:44.133765Z",
  "lastActiveAt": "2026-11-08T09:52:44.133765Z"
}
```

### Link request

`POST /api/devices/link-requests` takes the one-time public key, the code the new device shows and what it is called:

```json
{
  "temporaryPublicKey": "GJwEZa-wgX24cNhTOJhC_-msl16964_Qdq2Xjlh_Ip8",
  "code": "GJ6P3JDJF0",
  "kind": "Web",
  "name": "Safari on macOS"
}
```

It answers 201:

```json
{
  "id": "01a1ba93-16db-7e14-8a35-1c14c2ba5a9f",
  "expiresAt": "2026-11-08T10:02:44Z"
}
```

`GET /api/devices/link-requests?code=...` answers what the signed-in device is shown to approve:

```json
{
  "id": "01a1ba93-16db-7e14-8a35-1c14c2ba5a9f",
  "temporaryPublicKey": "GJwEZa-wgX24cNhTOJhC_-msl16964_Qdq2Xjlh_Ip8",
  "kind": "Web",
  "name": "Safari on macOS",
  "createdAt": "2026-11-08T09:52:44.251658Z",
  "expiresAt": "2026-11-08T10:02:44Z"
}
```

`GET /api/devices/link-requests/{id}` answers the state: `Pending` (waiting for approval), `Approved` with the sealed secrets, once, `Collected` (they were handed over) or `Expired`.

```json
{
  "state": "Pending",
  "payload": null,
  "expiresAt": "2026-11-08T10:02:44Z"
}
```

```json
{
  "state": "Approved",
  "payload": "AQG9lwUNCG8UxglA2cNxdeXl...",
  "expiresAt": "2026-11-08T10:02:44Z"
}
```

```json
{
  "state": "Collected",
  "payload": null,
  "expiresAt": "2026-11-08T10:02:44Z"
}
```

`POST /api/devices/link-requests/{id}/approve` takes `{ "payload": ... }` (the secrets sealed to the key of the request) and answers 204.

### Health

```json
{
  "status": "healthy",
  "checks": {
    "postgres": "healthy",
    "redis": "healthy"
  }
}
```
