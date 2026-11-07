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
  "traceId": "00-e7bcf4d444fe9ccc85805fe665721304-f92e50c943f3f49b-01"
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
  "traceId": "00-788b174f1cf4a2147d830a71146578b1-c5378bcd0b7408f9-01"
}
```

### 401 no token

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "traceId": "00-679cf62611e8e04f4cd9800b9cfb8843-498787f68d3748ea-01"
}
```

A wrong email or password is also a 401, with the same text for an unknown email and a wrong password:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Invalid email or password.",
  "traceId": "00-95c16ddbed2824c94fbb507ec746fe70-bf7aed9475dcca94-01"
}
```

### 403 not allowed

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "You are not a member of this conversation.",
  "traceId": "00-f43a518d4013a47cb8a740ffd0527410-77eefcb89fc98539-01"
}
```

A conversation that does not exist and one the caller is not in give this same answer. A missing `X-Requested-With` header on refresh and sign-out is a 403 too:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "The X-Requested-With header is required.",
  "traceId": "00-1900b4cc196f3b7ca280c57e06b2cd9b-4bdd958cc05e4569-01"
}
```

### 404 not found

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "User with ID '7b641906-ca02-41bb-bdd7-dc4e43773f71' was not found.",
  "traceId": "00-d125f29a2111292d143e50fa7ab4e44d-d44e41674e084218-01"
}
```

### 405

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.6",
  "title": "Method Not Allowed",
  "status": 405,
  "traceId": "00-e58470b05bae80c7dd802e73bf24b3ca-028a87b02f2fd15e-01"
}
```

### 409 conflict

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "This message was deleted.",
  "traceId": "00-84c28f57cef58e424ae28664399fc449-79a92723103abf74-01"
}
```

### 415

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.16",
  "title": "Unsupported Media Type",
  "status": 415,
  "traceId": "00-4092097886cd64d193345cb62588c873-9d15b7dfd3841709-01"
}
```

### 429 rate limit

Status 429, header `Retry-After: 59`:

```json
{
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Too many requests. Try again in 59 seconds.",
  "traceId": "00-beb80654aefd39302cd14706c8abc7dd-740f1401c1fd7f13-01"
}
```

### 429 account locked

After five wrong passwords the account is locked for 15 minutes. Status 429, header `Retry-After: 900`:

```json
{
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Too many failed sign-in attempts. Try again in 15 minutes.",
  "traceId": "00-9b04f6c51be6be8ff699fd373f5b62b6-2d7781b5e103cbed-01"
}
```

### 413 body too large

A request body over 1 MB is refused before it is read, with status 413.

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
| 503 | `/health` when Postgres or Redis cannot be reached. | the health check |

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
