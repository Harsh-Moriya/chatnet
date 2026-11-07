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
  "traceId": "00-0e5ec0a07ee9b793fa60b8d2ea81699a-4a59c5ef5a4de5a6-01"
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
  "traceId": "00-2b8498f0b75c87e08c57820cb8fffff5-710c8841e26b7b76-01"
}
```

### 401 no token

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "traceId": "00-5b43c4b247567d134aa7fc57a98aa1ca-87c787875e4de480-01"
}
```

A wrong email or password is also a 401, with the same text for an unknown email and a wrong password:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Invalid email or password.",
  "traceId": "00-f7fb12741b03cdec18b5f7dfafc421e0-19b6e6241181cf30-01"
}
```

### 403 not allowed

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "You are not a member of this conversation.",
  "traceId": "00-9de342c950b68be295697128e74b680e-7e844709cd0b8842-01"
}
```

A conversation that does not exist and one the caller is not in give this same answer. A missing `X-Requested-With` header on refresh and sign-out is a 403 too:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "The X-Requested-With header is required.",
  "traceId": "00-2adc32b7aa97bb92a3204746dcbe67af-45c8219c38009248-01"
}
```

### 404 not found

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "User with ID '94bce19b-da24-4db7-991c-5d8cb52c8b82' was not found.",
  "traceId": "00-dd1221499dba629cd1d14ad9a84bc7f2-1f4de14eca024dfd-01"
}
```

### 405

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.6",
  "title": "Method Not Allowed",
  "status": 405,
  "traceId": "00-b03d4c7749a464eb725b2582314d55eb-09459540a0587aab-01"
}
```

### 409 conflict

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "This message was deleted.",
  "traceId": "00-5927d81247d3835afdecda3abe0b8a27-75a64dd01ffe6d7a-01"
}
```

### 415

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.16",
  "title": "Unsupported Media Type",
  "status": 415,
  "traceId": "00-9179aba10ad5891c3207dc2b2f52b222-8f8200d8f154af8b-01"
}
```

### 429 rate limit

Status 429, header `Retry-After: 60`:

```json
{
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Too many requests. Try again in 60 seconds.",
  "traceId": "00-8fb4c471eebda8e924f7802814f41313-bbba6f89928124d1-01"
}
```

### 429 account locked

After five wrong passwords the account is locked for 15 minutes. Status 429, header `Retry-After: 900`:

```json
{
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Too many failed sign-in attempts. Try again in 15 minutes.",
  "traceId": "00-36263891c05db23954bb1a654332e8ff-b9fcf2d67d4fc488-01"
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
