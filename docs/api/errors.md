# Errors

Every error response is `application/problem+json` (RFC 9457, formerly RFC 7807), whoever produced it: a handler, the framework or the rate limiter. One writer produces them all.

## Fields

| Field | Meaning |
|---|---|
| `status` | The HTTP status, as a number. |
| `title` | A short, stable text for the status. |
| `detail` | A human-readable sentence that is safe to show to a person. Absent on a bare status response (a 401 without a token, a 404 for an unknown route). |
| `type` | A URI for the status class. Absent on a 429. |
| `traceId` | Matches the `traceId` in the server's logs and traces. Send it with a bug report. |
| `errors` | Only on a validation failure: a map from property name (PascalCase) to the messages for that field. |
| `code` | Only on some 409 responses: a short, stable name for the kind of conflict, so a client can tell them apart without reading `detail`. See [conflict codes](#conflict-codes). |

## Examples

Every body below is a real response.

### 400 validation failed

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation Failed",
  "status": 400,
  "detail": "The length of 'User Name' must be at least 3 characters. You entered 1 characters. 'Display Name' must not be empty. 'Email' is not a valid email address. The length of 'Password' must be at least 8 characters. You entered 5 characters.",
  "errors": {
    "UserName": [
      "The length of 'User Name' must be at least 3 characters. You entered 1 characters."
    ],
    "DisplayName": [
      "'Display Name' must not be empty."
    ],
    "Email": [
      "'Email' is not a valid email address."
    ],
    "Password": [
      "The length of 'Password' must be at least 8 characters. You entered 5 characters."
    ]
  },
  "traceId": "00-93ec8890402f4ebd65ea36f3f8158328-4e4454185b36cf8a-01"
}
```

`detail` joins the messages; `errors` is keyed by property for per-field display.

### 400 the request could not be read

Bad JSON, a value that is not a GUID, or a missing parameter. The detail is deliberately generic.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "The request could not be read. Check the URL, query string and JSON body.",
  "traceId": "00-834e22c461c40e7138c0107ecdfc86b0-846cdf635ec6db88-01"
}
```

### 401 no token

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "traceId": "00-67416ad0d49b033f671a9fe932aba4a6-76b6257c334467c7-01"
}
```

A wrong email or password is also a 401, with the same text for an unknown email and a wrong password:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Invalid email or password.",
  "traceId": "00-104bfa321f8f285e7e73d4faf587f701-2f7d4df5a09f1702-01"
}
```

### 403 not allowed

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "You are not a member of this conversation.",
  "traceId": "00-f06a0db5981f5e89c4ef99e2653f8bac-413cb180cf53c098-01"
}
```

A conversation that does not exist and one the caller is not in give this same answer. A missing `X-Requested-With` header on refresh and sign-out is a 403 too:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "The X-Requested-With header is required.",
  "traceId": "00-39022ac9a3ccbb246a29754fd81ef651-1e1cacf5a1659505-01"
}
```

Asking again for the account password to reset the encryption keys, with a password that is not right, is a 403 with a `code` the browser acts on. It is a 403 and not a 401 so the browser does not take it for an expired session. Repeated wrong passwords lock the account as signing in does (429):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "The password is not right.",
  "code": "wrong-password",
  "traceId": "00-9cf7669ed913d5d6c3cc60b878a0ebe8-8aa53a784c846b27-01"
}
```

### 404 not found

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "User with ID '6ee42f32-657f-4285-b05b-f65549abd766' was not found.",
  "traceId": "00-a4ee4138c3920b34fc589056071632e9-01401bb4ab33ffdd-01"
}
```

### 405

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.6",
  "title": "Method Not Allowed",
  "status": 405,
  "traceId": "00-eee972138a7f01fa388633baef7e1127-999471634e76598a-01"
}
```

### 409 conflict

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "This message was deleted.",
  "traceId": "00-dc82704402dd0afbefc92fd0cb0431b8-fe0dd9297c75f4ce-01"
}
```

### Conflict codes

A conflict about the keys of a conversation or the version of a message carries a `code`. For `epoch-stale`, `epoch-members-changed` and `epoch-due` the client should look at the conversation again and make or read the epoch anew; for `edit-count-stale` it should read the message again.

| `code` | Meaning |
|---|---|
| `epoch-stale` | Another epoch was stored first, so the one offered is not the next number. For a message or an edit: the conversation is not in the epoch it was sealed under. |
| `epoch-due` | A message or an edit was sealed under the epoch in force, but a removal or a leave marked that epoch for replacement, so nothing more is stored under it until a new one is made. |
| `edit-count-stale` | The edit was made from an older version of the message: another edit was stored first. |
| `generation-stale` | The bundle is not the next generation of the account key: another device stored one first. The browser reads its keys again. |
| `backup-version-stale` | The backup was replaced from another device since the version a new generation was made from. The browser reads it again. |
| `changes-cursor-ahead` | The point asked from is past the newest change the server has, which means the server holds an older state than the client last saw. The client reads the conversation again from the start. |
| `epoch-members-changed` | The people in the conversation, or the key generation of one of them, are not what the epoch was made for. |
| `member-has-no-keys` | Someone in the conversation has not set up encryption, so no key can be sealed for them. |
| `epoch-too-soon` | The keys of the conversation were replaced less than a day ago and nothing asks for them to be replaced again (a removal or a leave does). Looking again does not help. |

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "The people in this conversation, or their keys, are not the ones this epoch was made for.",
  "code": "epoch-members-changed",
  "traceId": "00-8fbb2e4917543416697f8f5f2f2afece-f80eb2b719a991bd-01"
}
```

A message under an epoch that is not the one the conversation is in:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "The keys of this conversation changed. Look at the conversation again.",
  "code": "epoch-stale",
  "traceId": "00-81c1fd3c5cbb94578706ee0eb7d4e29b-392549f6581f9ed4-01"
}
```

A message under an epoch that a removal marked for replacement:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "The keys of this conversation must be replaced before anything more is said.",
  "code": "epoch-due",
  "traceId": "00-ddd9871cee1f51c831a0b7ad7a84c109-d777eeff3dc0b321-01"
}
```

An edit made from an older version of the message:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "This message was edited somewhere else first. Look at its latest version.",
  "code": "edit-count-stale",
  "traceId": "00-024c90f9f4936872fd5a01e45bc900a0-276b2ffe56deb837-01"
}
```

A message that is not a well formed sealed message, and a send with no client message id:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation Failed",
  "status": 400,
  "detail": "The message is not a well formed encrypted message.",
  "errors": {
    "Ciphertext": [
      "The message is not a well formed encrypted message."
    ]
  },
  "traceId": "00-90daecba6c1c623d3b2c9d60b8755f04-a0a804b9a50b2c43-01"
}
```

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation Failed",
  "status": 400,
  "detail": "'Client Message Id' must not be empty.",
  "errors": {
    "ClientMessageId": [
      "'Client Message Id' must not be empty."
    ]
  },
  "traceId": "00-c2fb81067e1687d3a2a32958d5f8d03e-c817f39b4335dd22-01"
}
```

### 415

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.16",
  "title": "Unsupported Media Type",
  "status": 415,
  "traceId": "00-7dd3e4f4af4f483551c39fbebe5eb75f-4fea0cc63cad1f48-01"
}
```

### 429 rate limit

Status 429, header `Retry-After: 59`:

```json
{
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Too many requests. Try again in 59 seconds.",
  "traceId": "00-408456452030b08c5f4f8cb951c82134-df3d1a0b6a340c82-01"
}
```

### 429 account locked

After five wrong passwords the account is locked for 15 minutes. Status 429, header `Retry-After: 900`:

```json
{
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Too many failed sign-in attempts. Try again in 15 minutes.",
  "traceId": "00-ffc7af98d434ddc730b01be8ae797827-0e3a80a1980a764a-01"
}
```

### 400 a reset link that cannot be used

An unknown, used, expired or voided link, and a link for a deactivated account, all give this one answer, on the field `Token`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation Failed",
  "status": 400,
  "detail": "This link is invalid or has expired.",
  "errors": {
    "Token": [
      "This link is invalid or has expired."
    ]
  },
  "traceId": "00-aa14c329806e7ef468da4600f73e7125-3d2f5f0ddef96670-01"
}
```

A new password that breaks an account rule is reported on `NewPassword` and leaves the link usable:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation Failed",
  "status": 400,
  "detail": "Passwords must have at least one uppercase ('A'-'Z').",
  "errors": {
    "NewPassword": [
      "Passwords must have at least one uppercase ('A'-'Z')."
    ]
  },
  "traceId": "00-883230e8f6c31a1042c33ee76347dcd0-5c189f2f67ba9bcd-01"
}
```

### 413 body too large

A request body over 1 MB is refused before it is read, with status 413.

### 503 not available

A feature this deployment is not set up for. Today that is asking for a password reset link when the server has no mail relay (`Email:Smtp:Host` is not set):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.4",
  "title": "Service Unavailable",
  "status": 503,
  "detail": "Password reset is not available.",
  "traceId": "00-5e8925864638f03b712eedd4b496fff7-c4b24fa26356d5a9-01"
}
```

### 500

An unexpected failure. The client sees a generic detail; the log has the exception.

```json
{ "title": "An unexpected error occurred", "status": 500,
  "detail": "An unexpected error occurred. Please try again later.", "traceId": "00-..." }
```

## Status codes

| Status | Used for | Produced by |
|---|---|---|
| 400 | Validation failed, or the request could not be read. A password rule failure is reported on the `Password` field. A cursor that is not the caller's own is also a 400. | validation, the framework |
| 401 | No or invalid token; wrong email or password. | the JWT middleware, `UnauthorizedException` |
| 403 | Authenticated but not allowed: not an active member (also for an unknown conversation, on purpose), not allowed by role, a deactivated account, a missing `X-Requested-With`. | `ForbiddenException` |
| 404 | The resource or route does not exist, or is hidden from the caller (an attachment, a message sent before they joined, a notification that is someone else's). | `NotFoundException`, routing |
| 405, 415 | Wrong method, wrong content type. | the framework |
| 409 | Conflicts with the current state: a name or email taken, a direct chat where a group is needed, a deleted message, a closed edit window, a refresh that lost a race. | `ConflictException` |
| 413 | Body over 1 MB. | Kestrel |
| 429 | A rate limit, or a locked account. Both set `Retry-After`. | the rate limiter, `TooManyAttemptsException` |
| 500 | Unexpected. | anything else |
| 503 | `/health` when Postgres or Redis cannot be reached; asking for a password reset link when no mail relay is set up. | the health check, `ServiceUnavailableException` |

## Rules for client code

- Switch on `status`. Show `detail` when present. Use `errors` to mark fields. Never parse `title` or `detail` for logic.
- Wait `Retry-After` seconds before repeating a request that got a 429.
- A 409 from `/api/auth/refresh` means another request replaced the token a moment ago: try again.
- A 401 on a request with a token means the access token expired: renew it once and repeat the request.

## Hub errors

A refused hub call surfaces as a `HubException` whose message is safe to show. With the JavaScript client the text reads:

```
An unexpected error occurred invoking 'JoinConversation' on the server. HubException: You are not a member of this conversation.
```

so match on the final sentence (`You are not a member of this conversation.`) rather than the whole string. The same refusal is given for an unknown conversation and one that belongs to someone else.
