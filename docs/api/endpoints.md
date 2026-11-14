# Endpoints

The HTTP API. In Development the running API also serves its OpenAPI document at `/openapi/v1.json` and an interactive reference at `/scalar/v1`; every operation there declares its success body and each error it can return. The tables below list every route (60 operations) with its authentication, its rate-limit policy and what it does. Request and response bodies in the examples were copied from a running API.

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
| `POST /api/conversations/{conversationId}/key-epochs` | Bearer | keys | 204, 400, 401, 403, 409, 429 | Stores the next epoch (`epoch`, `commitment`, `signature`, `envelopes`). Any member. 204. 409 with a `code`: `epoch-stale` (not the next number), `epoch-members-changed` (not for exactly the people in the conversation, or sealed to an old key generation), `member-has-no-keys`, or `epoch-too-soon` (the newest epoch is less than a day old and nothing asks for a replacement). |

## Messages

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `POST /api/conversations/{conversationId}/messages` | Bearer | send | 200, 201, 400, 401, 403, 409, 429 | Sends a message sealed in the browser (`clientMessageId`, `ciphertext`, `mentionedUserIds`, and `attachments`, each only `{ uploadKey }`: what a file is called and is lies inside `ciphertext`). 201 for a new message, 200 with the stored message when this `clientMessageId` was already sent. 409 with `epoch-stale` or `epoch-due` when the conversation is not in the epoch the message was sealed under, or that epoch is marked for replacement. |
| `GET /api/conversations/{conversationId}/messages` | Bearer |  | 200, 400, 401, 403 | History, at most 50, oldest first. At most one of `before`, `after`, `around` (a message id). |
| `GET /api/conversations/{conversationId}/changes` | Bearer |  | 200, 400, 401, 403, 409 | The messages changed after a point (`since`, the number of the last change the client has seen, 0 for none; required), in the order the changes were made, at most 100, with `cursor` to continue from and `more`. A message appears once, in its current state; a deleted one comes with `ciphertext: null`. The member window of the history applies. 409 `changes-cursor-ahead` when `since` is past the newest change the server has. See [searching messages](../features/message-search.md#the-changes-feed). |
| `PATCH /api/conversations/{conversationId}/messages/{messageId}` | Bearer | edit | 200, 400, 401, 403, 404, 409, 429 | Edits the caller's own message within the edit window, with a new sealed text (`ciphertext`, `editCount`, `mentionedUserIds`). `editCount` must be one more than the message has now, otherwise 409 `edit-count-stale`; the epoch rules of a send apply. |
| `DELETE /api/conversations/{conversationId}/messages/{messageId}` | Bearer |  | 200, 401, 403, 404 | Deletes a message. The sender at any time; a group owner or admin for anyone's. |

## Files

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `POST /api/conversations/{conversationId}/uploads` | Bearer | uploads | 200, 400, 401, 403, 429 | Ticket for uploading one encrypted attachment straight to the bucket (`sizeBytes`, the size of the encrypted file: 29 bytes to what a file of `Storage:MaxFileBytes` becomes). The upload is signed for `application/octet-stream`. |
| `GET /api/attachments/{attachmentId}/url` | Bearer |  | 200, 401, 404 | A new signed link for a file the caller may read. One 404 for every refusal. |

## Notifications

| Method and route | Auth | Limit | Statuses | Description |
|---|---|---|---|---|
| `GET /api/notifications` | Bearer |  | 200, 400, 401 | The caller's notifications, 30 per page, newest first (`before` is a notification id), with `unreadCount` (stops at 100). A mention carries its message sealed. |
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
| `POST /api/keys/me/generations` | Bearer | keys | 200, 400, 401, 404, 409, 429 | The next generation of the account key with the backup that holds it (`bundle`, `backup`, `expectedBackupVersion`) and, when a device was lost, the device to remove (`removeDeviceId`), in one step: the device is removed with its sessions and every conversation the caller is in is marked for a new key period. 200 with the state. 409 with `generation-stale` when the bundle is not the next generation and `backup-version-stale` when the backup was changed since that version; 404 for a device that is not the caller's and before set-up. See [encryption keys](../features/encryption-keys.md). |
| `POST /api/keys/me/reset` | Bearer | keys | 200, 400, 401, 403, 404, 409, 429 | Starts the keys over for a person with neither a device nor the passphrase: the next generation from new keys and a new backup (`bundle`, `backup`), confirmed with the account password (`password`). Every device is removed and every conversation the caller is in is marked for a new key period. 200 with the state. 403 with `wrong-password`, 429 while the account is locked, 409 with `generation-stale`. See [encryption keys](../features/encryption-keys.md). |
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
  "id": "b7e9b446-5fa3-48d5-a5bc-7e3b50a5a940",
  "type": "Direct",
  "name": "Ben Example",
  "avatarUrl": null,
  "updatedAt": "2026-11-14T06:24:24.075107Z",
  "lastMessage": null,
  "unreadCount": 0,
  "otherUserId": "01a1d8ba-7e2a-7cfc-bdac-fc7da1085002",
  "isMuted": false,
  "leftAt": null,
  "keyEpoch": 1,
  "keyRotationRequired": false,
  "keyEpochAt": "2026-11-14T06:24:24.075107Z",
  "changeSeq": 0
}
```

`keyEpoch` is the newest key epoch of the conversation, 0 while it has none, and `keyRotationRequired` says someone left since it was made. See [conversation keys](../features/conversation-keys.md).

For a direct chat `name` is the other person's display name and `otherUserId` is their id. For a group, `otherUserId` is null. `lastMessage` is null for an empty conversation, otherwise the last message as it was sealed, for the browser to open and draw the preview (`ciphertext` is null when the last message was deleted):

```json
{
  "senderId": "01a1d8b9-4dc1-750f-a3ba-4b6b8e74bd35",
  "ciphertext": "AQEAAAABDsORYX32e315guJO...",
  "keyEpoch": 1,
  "clientMessageId": "32707844-e45a-4eb7-8369-f949ea0e2070",
  "editCount": 0,
  "createdAt": "2026-11-14T06:23:10.555082Z",
  "attachmentCount": 0
}
```

### Message

`ciphertext` is the sealed text as unpadded base64url, and `keyEpoch` the epoch whose key opens it. Long values are shortened here.

```json
{
  "id": "01a1d8b9-5fc5-7a5b-b775-cd8f6f694b64",
  "conversationId": "99924b68-906d-46df-ae3f-53af02f0cdd5",
  "senderId": "01a1d8b9-4dc1-750f-a3ba-4b6b8e74bd35",
  "senderDisplayName": "Ana Example",
  "ciphertext": "AQEAAAABTiYzANbNRvxLKr6H...",
  "keyEpoch": 1,
  "createdAt": "2026-11-14T06:23:09.764167Z",
  "clientMessageId": "84bb8acc-33b9-48fa-b643-f04bca173e6c",
  "attachments": [],
  "editedAt": null,
  "editCount": 0,
  "isDeleted": false,
  "changeSeq": 1
}
```

A message with a file carries its attachments, each an id, the stored size (the size of the encrypted file) and a link. What a file is called and is, and the key that opens it, are inside `ciphertext`:

```json
{
  "id": "01a1d8b9-6b1d-79e3-b938-f474e3e71eef",
  "sizeBytes": 2077,
  "url": "http://localhost:9000/chatnet/attachments/99924b68-906d-46df-ae3f-53af02f0cdd5/01a1d8b9-6b1d-79e3-b938-f474e3e71eef?<signature parameters>"
}
```

A deleted message has `ciphertext: null`, `isDeleted: true` and no attachments.

`changeSeq` is the number the conversation gave to the latest change of the message (its send, its latest edit or its delete). It is unique within the conversation, and a conversation in the list carries the number of its latest change as `changeSeq`.

### Changes

`GET /api/conversations/{id}/changes?since=5`, after an edit and a delete (the edited message was then deleted, so it appears once, at the number of its delete):

```json
{
  "changes": [
    {
      "id": "01a1d8b9-6b9e-7ed5-bce9-af33dff59784",
      "conversationId": "99924b68-906d-46df-ae3f-53af02f0cdd5",
      "senderId": "01a1d8b9-4dc1-750f-a3ba-4b6b8e74bd35",
      "senderDisplayName": "Ana Example",
      "ciphertext": "AQEAAAABZlJrYaqozEWuTUHW...",
      "keyEpoch": 1,
      "createdAt": "2026-11-14T06:23:12.345055Z",
      "clientMessageId": "65e9c18f-1214-49e0-a19d-6e9f8b163f6f",
      "attachments": [
        {
          "id": "01a1d8b9-6b1d-79e3-b938-f474e3e71eef",
          "sizeBytes": 2077,
          "url": "http://localhost:9000/chatnet/attachments/99924b68-906d-46df-ae3f-53af02f0cdd5/01a1d8b9-6b1d-79e3-b938-f474e3e71eef?<signature parameters>"
        }
      ],
      "editedAt": null,
      "editCount": 0,
      "isDeleted": false,
      "changeSeq": 7
    },
    {
      "id": "01a1d8b9-5fc5-7a5b-b775-cd8f6f694b64",
      "conversationId": "99924b68-906d-46df-ae3f-53af02f0cdd5",
      "senderId": "01a1d8b9-4dc1-750f-a3ba-4b6b8e74bd35",
      "senderDisplayName": "Ana Example",
      "ciphertext": null,
      "keyEpoch": 1,
      "createdAt": "2026-11-14T06:23:09.764167Z",
      "clientMessageId": "84bb8acc-33b9-48fa-b643-f04bca173e6c",
      "attachments": [],
      "editedAt": "2026-11-14T06:23:11.521043Z",
      "editCount": 1,
      "isDeleted": true,
      "changeSeq": 8
    }
  ],
  "cursor": 8,
  "more": false
}
```

`cursor` is where the next request starts: the last number returned while `more` is true, and the conversation's number as it was read when `more` is false.

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
  "uploadKey": "pending/99924b68-906d-46df-ae3f-53af02f0cdd5/01a1d8b9-4dc1-750f-a3ba-4b6b8e74bd35/f5844b70-91ce-445f-97a1-b7fe0d2f6dff",
  "url": "http://localhost:9000/chatnet/pending/99924b68-906d-46df-ae3f-53af02f0cdd5/01a1d8b9-4dc1-750f-a3ba-4b6b8e74bd35/f5844b70-91ce-445f-97a1-b7fe0d2f6dff?<signature parameters>",
  "expiresAt": "2026-11-14T06:33:11Z"
}
```

`url` is a presigned PUT to the bucket, valid for 10 minutes. A file ticket is signed for `application/octet-stream` whatever the file is, since the file is encrypted; a picture ticket is signed for the declared content type.

### Attachment link

`GET /api/attachments/{id}/url`:

```json
{
  "url": "http://localhost:9000/chatnet/attachments/99924b68-906d-46df-ae3f-53af02f0cdd5/01a1d8b9-6b1d-79e3-b938-f474e3e71eef?<signature parameters>",
  "expiresAt": "2026-11-14T07:23:13Z"
}
```

### Notifications

`GET /api/notifications`:

A mention carries the message it is about as `message`, sealed as it was sent (null for an invitation, and once the message is deleted). The person who sealed it is the `actorId`.

```json
{
  "items": [
    {
      "id": "01a1d8b9-61c0-7405-bd7e-c4b90cde583d",
      "type": "Mention",
      "conversationId": "99924b68-906d-46df-ae3f-53af02f0cdd5",
      "actorId": "01a1d8b9-5307-73df-aa61-05adbc82681b",
      "actorDisplayName": "Ben Example",
      "messageId": "01a1d8b9-6199-70a7-8f8b-5c3de906f8ae",
      "message": {
        "ciphertext": "AQEAAAAB734U_6pZGVjg3ccc...",
        "keyEpoch": 1,
        "clientMessageId": "930d9700-bf92-4733-979a-f2361fb39adf",
        "editCount": 0
      },
      "isRead": false,
      "createdAt": "2026-11-14T06:23:10.233738Z"
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
  "bundle": "AQEAAAABjYFGydQxokbA6jgz...",
  "backup": "AQEBVZMrvHbbkByXaRv2TN6U..."
}
```

```json
{
  "bundles": [
    {
      "generation": 1,
      "bundle": "AQEAAAABjYFGydQxokbA6jgz..."
    }
  ],
  "backup": {
    "version": 1,
    "updatedAt": "2026-11-13T15:39:00.197642Z"
  }
}
```

`GET /api/keys/me/backup`:

```json
{
  "blob": "AQEBVZMrvHbbkByXaRv2TN6U...",
  "version": 1,
  "updatedAt": "2026-11-13T15:39:00.197642Z"
}
```

`PUT /api/keys/me/backup` takes `{ "blob": ..., "expectedVersion": 1 }` and answers:

```json
{
  "version": 2,
  "updatedAt": "2026-11-13T15:39:00.287724Z"
}
```

`POST /api/keys/me/generations` takes the next generation, the backup that holds it, the version of the backup it was made from and the device that was lost, and answers with the state:

```json
{
  "bundle": "AQEAAAAC86jfsA4IrP8GLCbb...",
  "backup": "AQEBxlGAxgQrRArTKbelvDjs...",
  "expectedBackupVersion": 2,
  "removeDeviceId": "01a1d58f-e8d7-79de-87dc-a86fb6351e13"
}
```

```json
{
  "bundles": [
    {
      "generation": 1,
      "bundle": "AQEAAAABjYFGydQxokbA6jgz..."
    },
    {
      "generation": 2,
      "bundle": "AQEAAAAC86jfsA4IrP8GLCbb..."
    }
  ],
  "backup": {
    "version": 3,
    "updatedAt": "2026-11-13T15:39:00.725033Z"
  }
}
```

`POST /api/keys/me/reset` takes new keys and the account password and answers with the state:

```json
{
  "bundle": "AQEAAAADx0YOo6uCg2Zah3zj...",
  "backup": "AQEBVED8QSIEZipS1Y1B9xVh...",
  "password": "<account password>"
}
```

```json
{
  "bundles": [
    {
      "generation": 1,
      "bundle": "AQEAAAABjYFGydQxokbA6jgz..."
    },
    {
      "generation": 2,
      "bundle": "AQEAAAAC86jfsA4IrP8GLCbb..."
    },
    {
      "generation": 3,
      "bundle": "AQEAAAADx0YOo6uCg2Zah3zj..."
    }
  ],
  "backup": {
    "version": 4,
    "updatedAt": "2026-11-13T15:39:01.073233Z"
  }
}
```

### Conversation keys

`POST /api/keys/lookup` takes `{ "userIds": [...] }` and answers the current bundle of each person who has keys:

```json
{
  "userIds": [
    "01a1d8ba-7e2a-7cfc-bdac-fc7da1085002",
    "01a1d8ba-7ec1-7ac0-b682-716f2af43bde"
  ]
}
```

```json
{
  "bundles": [
    {
      "userId": "01a1d8ba-7e2a-7cfc-bdac-fc7da1085002",
      "generation": 1,
      "bundle": "AQEAAAABoCl4QxMh8GDqZF-S..."
    },
    {
      "userId": "01a1d8ba-7ec1-7ac0-b682-716f2af43bde",
      "generation": 1,
      "bundle": "AQEAAAABwrT6rcBwQmS4dRL7..."
    }
  ]
}
```

A new conversation, and a group that people are added to, carry the epoch. `POST /api/conversations/direct` takes the id the caller chose, the other person and the first epoch:

```json
{
  "id": "b7e9b446-5fa3-48d5-a5bc-7e3b50a5a940",
  "otherUserId": "01a1d8ba-7e2a-7cfc-bdac-fc7da1085002",
  "epoch": {
    "epoch": 1,
    "commitment": "UmaqWfxc8hilAIFJ7SkWCusEc-s2Jo9FlNODY930T6s",
    "signature": "Tu73kK3XD-NSuKSGFntAmXpE...",
    "envelopes": [
      {
        "userId": "01a1d8ba-7da0-7519-84e0-930bb2e54500",
        "generation": 1,
        "envelope": "AQFip7_pUb1OtpAaQrBlFfhb..."
      },
      {
        "userId": "01a1d8ba-7e2a-7cfc-bdac-fc7da1085002",
        "generation": 1,
        "envelope": "AQGlbqr-joITBzI617-YLEp9..."
      }
    ]
  }
}
```

`POST /api/conversations/{id}/key-epochs` takes the epoch itself (the same object) and answers 204, unless the conversation already has an epoch that is less than a day old and `keyRotationRequired` is false: then it is a 409 with the code `epoch-too-soon`. An epoch that comes with a new conversation or with added people is not held back. `GET` answers a page of the epochs the caller has an envelope for:

```json
{
  "epochs": [
    {
      "epoch": 1,
      "creatorId": "01a1d8ba-7da0-7519-84e0-930bb2e54500",
      "reason": "Created",
      "createdAt": "2026-11-14T06:24:24.187318Z",
      "commitment": "YK6RAW6S72BaDF8moO34tV8c3Htz1mCxBv8NL762gkw",
      "signature": "rDR5wcu6Ad6A1KGiyI1ERvPF...",
      "members": [
        {
          "userId": "01a1d8ba-7da0-7519-84e0-930bb2e54500",
          "generation": 1
        },
        {
          "userId": "01a1d8ba-7e2a-7cfc-bdac-fc7da1085002",
          "generation": 1
        },
        {
          "userId": "01a1d8ba-7ec1-7ac0-b682-716f2af43bde",
          "generation": 1
        }
      ],
      "generation": 1,
      "envelope": "AQGwkvWhGaU3lHF2HaomtPs0..."
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
  "traceId": "00-ae6e1ee38371e21584f6a903af05ee0a-c0bb43a209f50fbb-01"
}
```

### Device

`POST /api/devices` takes `{ "kind": "Web", "name": "Chrome on Windows" }` and answers 201 with the device; `GET /api/devices` answers a list of these:

```json
{
  "id": "01a1d58f-e792-7f31-8a9f-f51ea69deb00",
  "kind": "Web",
  "name": "Chrome on Windows",
  "createdAt": "2026-11-13T15:39:00.370907Z",
  "lastActiveAt": "2026-11-13T15:39:00.370907Z"
}
```

### Link request

`POST /api/devices/link-requests` takes the one-time public key, the code the new device shows and what it is called:

```json
{
  "temporaryPublicKey": "NRaRbLtybUZvvyHPhg_lBPKNenTQPfzGSlVK6k9q98E",
  "code": "KJNJBQKRAZ",
  "kind": "Web",
  "name": "Safari on macOS"
}
```

It answers 201:

```json
{
  "id": "01a1d58f-e813-7878-bd2e-a443a74e2c46",
  "expiresAt": "2026-11-13T15:49:00Z"
}
```

`GET /api/devices/link-requests?code=...` answers what the signed-in device is shown to approve:

```json
{
  "id": "01a1d58f-e813-7878-bd2e-a443a74e2c46",
  "temporaryPublicKey": "NRaRbLtybUZvvyHPhg_lBPKNenTQPfzGSlVK6k9q98E",
  "kind": "Web",
  "name": "Safari on macOS",
  "createdAt": "2026-11-13T15:39:00.499529Z",
  "expiresAt": "2026-11-13T15:49:00Z"
}
```

`GET /api/devices/link-requests/{id}` answers the state: `Pending` (waiting for approval), `Approved` with the sealed secrets, once, `Collected` (they were handed over) or `Expired`.

```json
{
  "state": "Pending",
  "payload": null,
  "expiresAt": "2026-11-13T15:49:00Z"
}
```

```json
{
  "state": "Approved",
  "payload": "AQHt5A9lS3s8jMP3lBfHqkxG...",
  "expiresAt": "2026-11-13T15:49:00Z"
}
```

```json
{
  "state": "Collected",
  "payload": null,
  "expiresAt": "2026-11-13T15:49:00Z"
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
