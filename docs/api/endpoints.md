# Endpoints

The HTTP API. In Development the running API also serves its OpenAPI document at `/openapi/v1.json` and an interactive reference at `/scalar/v1`; every operation there declares its success body and each error it can return. The tables below list every route (44 operations) with its authentication, its rate-limit policy and what it does. Request and response bodies in the examples were copied from a running API.

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
| `POST /api/auth/login` | None | auth | 200, 400, 401, 403, 429 | Signs in. 401 for a wrong email or password (same text), 403 for a deactivated account, 429 for the rate limit or a locked account. |
| `POST /api/auth/refresh` | Refresh cookie | refresh | 200, 401, 403, 409, 429 | Exchanges the refresh cookie for a new access token and a new cookie; the old refresh token stops working. Needs `X-Requested-With: ChatNET`. 409 means another request replaced the token a moment ago: retry. |
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
| `POST /api/conversations/direct` | Bearer |  | 200, 400, 401, 404 | Finds or creates the direct chat with `otherUserId`. Safe to repeat. |
| `POST /api/conversations/group` | Bearer |  | 201, 400, 401, 404, 409 | Creates a group (`name`, `memberIds`). The caller is the owner. 201 with a `Location` header. |
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
| `POST /api/conversations/{conversationId}/members` | Bearer |  | 200, 400, 401, 403, 404, 409 | Adds people (`userIds`, 1 to 100). Owner or admin. Answers the members after the change. |
| `DELETE /api/conversations/{conversationId}/members/{userId}` | Bearer |  | 200, 401, 403, 404, 409 | Removes a member. The owner removes anyone; an admin removes plain members. |
| `PUT /api/conversations/{conversationId}/members/{userId}/role` | Bearer |  | 200, 400, 401, 403, 404, 409 | Sets a role (`Owner`, `Admin` or `Member`). Owner only; `Owner` hands the group over. |
| `POST /api/conversations/{conversationId}/leave` | Bearer |  | 200, 401, 403, 409 | Leaves a group. Answers `leftAt`. An owner who leaves hands the group to the longest-standing admin, else member. |

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

## Shapes

Examples are real responses. Where a list is shown, it is cut to its first entry.

### AuthResponse

`POST /api/auth/register`, `/login` and `/refresh` answer:

```json
{
  "token": "<access token>",
  "userId": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
  "userName": "ana_hlqpp",
  "displayName": "Ana Example",
  "accessTokenExpiresAt": "2026-11-07T09:07:35Z"
}
```

The refresh token is in the `Set-Cookie` header, not the body: `chatnet_refresh=<token>; path=/api/auth; secure; samesite=strict; httponly`.

### UserSummary

`GET /api/users` (a list of these), `GET` and `PATCH /api/users/me`:

```json
{
  "id": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
  "userName": "ana_hlqpp",
  "displayName": "Ana Example",
  "avatarUrl": null
}
```

`avatarUrl` is a relative address (`/api/avatars/users/{ownerId}/{fileId}`) or null.

### Conversation

```json
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
```

For a direct chat `name` is the other person's display name and `otherUserId` is their id. For a group, `otherUserId` is null. `lastMessage` is null for an empty conversation, otherwise:

```json
{
  "senderId": "01a1b535-abad-77a7-a8ad-aad0ec6c1972",
  "content": "Sounds good 3",
  "createdAt": "2026-11-07T08:52:38.95065Z",
  "attachmentCount": 0
}
```

### Message

```json
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
```

A message with a file carries its attachments:

```json
{
  "id": "01a1b535-bd0c-7499-948d-1e64c60e58c8",
  "type": "Image",
  "fileName": "map.png",
  "contentType": "image/png",
  "sizeBytes": 70,
  "url": "http://localhost:9000/chatnet/attachments/01a1b535-b41e-761a-91ea-5cb6239f7221/01a1b535-bd0c-7499-948d-1e64c60e58c8?<signature parameters>"
}
```

A deleted message has `content: null`, `isDeleted: true` and no attachments.

### Member

`GET /api/conversations/{id}/members` and the group-changing calls answer a list of these:

```json
[
  {
    "userId": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
    "userName": "ana_hlqpp",
    "displayName": "Ana Example",
    "avatarUrl": null,
    "role": "Owner",
    "lastReadAt": "2026-11-07T08:52:38.808191Z"
  }
]
```

### Group update and leave

`PATCH /api/conversations/{id}`:

```json
{
  "id": "01a1b535-b41e-761a-91ea-5cb6239f7221",
  "name": "Weekend plans (Sunday)",
  "avatarUrl": null
}
```

`POST /api/conversations/{id}/leave`:

```json
{
  "leftAt": "2026-11-07T08:52:41.574666Z"
}
```

### UploadTicket

`POST .../uploads` and `POST .../avatar/uploads`:

```json
{
  "uploadKey": "pending/01a1b535-b41e-761a-91ea-5cb6239f7221/01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c/466f5d5b-edc8-40bb-a32a-ea6132ed489a",
  "url": "http://localhost:9000/chatnet/pending/01a1b535-b41e-761a-91ea-5cb6239f7221/01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c/466f5d5b-edc8-40bb-a32a-ea6132ed489a?<signature parameters>",
  "expiresAt": "2026-11-07T09:02:39Z"
}
```

`url` is a presigned PUT to the bucket, valid for 10 minutes and for exactly the declared content type.

### Attachment link

`GET /api/attachments/{id}/url`:

```json
{
  "url": "http://localhost:9000/chatnet/attachments/01a1b535-b41e-761a-91ea-5cb6239f7221/01a1b535-bd0c-7499-948d-1e64c60e58c8?<signature parameters>",
  "expiresAt": "2026-11-07T09:52:40Z"
}
```

### Notifications

`GET /api/notifications`:

```json
{
  "items": [
    {
      "id": "01a1b535-b746-7d09-8a48-5ba47a7e75a0",
      "type": "Mention",
      "conversationId": "01a1b535-b41e-761a-91ea-5cb6239f7221",
      "actorId": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
      "actorDisplayName": "Ana Example",
      "messageId": "01a1b535-b718-7856-b9d7-e488b9274463",
      "messagePreview": "@ben_hlqpp can you book a table?",
      "isRead": false,
      "createdAt": "2026-11-07T08:52:38.808191Z"
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
    "userId": "01a1b535-abad-77a7-a8ad-aad0ec6c1972",
    "isOnline": true,
    "lastSeenAt": null,
    "at": "2026-11-07T08:52:39.541Z"
  }
]
```

### Search hit

A hit found by its text:

```json
[
  {
    "messageId": "01a1b535-bd63-7c02-a405-e9a37141e284",
    "conversationId": "01a1b535-b41e-761a-91ea-5cb6239f7221",
    "conversationName": "Weekend plans (Sunday)",
    "conversationType": "Group",
    "senderId": "01a1b535-a9eb-74c3-ad63-7d4ce3e5a23c",
    "senderDisplayName": "Ana Example-Jones",
    "createdAt": "2026-11-07T08:52:40.092618Z",
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
  "endpoint": "https://fcm.googleapis.com/fcm/send/example-hlqpp",
  "keys": {
    "p256dh": "BJ8Vt4V5wG_-kks91CspLt_PAt08tPDlpgmh4kfMcoB1ciNFXHcIX-epmzq8ZuWq5e56qvQJ8XXMCdyWpeHAByk",
    "auth": "I3gmnvKWizrTB8lPDtS_sQ"
  }
}
```

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
