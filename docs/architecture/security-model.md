# Security model

What protects the system, how each protection works, and where its limits are. The limits are stated on purpose: a protection described without its limit is a claim, not a design.

## Identity and sessions

| Concern | How it is handled | Limit |
|---|---|---|
| Who is calling | A JWT access token, signed HS256, validated for signature, issuer, audience and lifetime. It lives 15 minutes and is kept in memory only. Claims: `sub`, `email`, `unique_name`, `display_name`, `jti`. | A token cannot be taken back. A deactivated user keeps what they hold for at most 15 minutes and is not renewed; sending is refused for them at once. An expired token is rejected after a 30-second tolerance. |
| Staying signed in | A refresh token: 32 random bytes in the `chatnet_refresh` cookie, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/api/auth`. Only its SHA-256 hash is stored. Each refresh retires the token and issues the next in the same family. A retired token presented again after 30 seconds revokes the whole family. | A thief who refreshes first and an owner who refreshes second are both signed out, and nothing says which was which. A copied token that is never used again goes unnoticed until it expires (14 days). A refresh whose reply is lost leaves the browser with a retired token; presented after the grace period it looks like theft and the session ends. |
| Cross-site requests to refresh and sign-out | `SameSite=Strict` keeps the cookie off cross-site requests, and both endpoints also refuse a request without `X-Requested-With: ChatNET`. | |
| Passwords | Identity's salted PBKDF2 hashing, never logged. At least 8 characters with a digit, a lowercase and an uppercase letter. Email length is capped at 254 and password length at 128, so a request cannot ask the server to hash an enormous value. | |
| Guessing passwords | A per-address rate limit on sign-in (10 per minute) and account lockout (5 failures, 15 minutes). | While Redis cannot be reached the rate limit is not applied and only the lockout, stored in Postgres, protects a password. |
| User enumeration | An unknown email and a wrong password give the same 401 text, and an unknown email costs the same password check as a wrong one. | Registration reveals which emails exist (a 409). |
| Account creation | Registration takes a Postgres advisory lock on the email, so simultaneous registrations for one email run one at a time. A unique index on the normalised email is the final guard. | |
| Tokens in the browser | The access token lives in memory; the refresh token is in a cookie scripts cannot read; nothing is written to browser storage. | A script injected into the page can use the access token while the page is open (15 minutes at most). It cannot copy the long-lived credential. |
| Tokens in URLs | Accepted from `?access_token=` only on `/hubs/chat`. | Query strings can appear in proxy logs; the production proxy's logs are configured to leave the token out. |

## Authorization

Every endpoint requires authentication unless it is marked anonymous (sign-in, registration, refresh, sign-out, `/health`, and the two picture routes). The set of anonymous endpoints is pinned by a test.

- **Identity comes from the token.** A request body never says who the caller is.
- **Membership is checked in the handler or the hub**, never trusted from the client. A conversation that does not exist and one the caller is not in get the identical 403, so ids reveal nothing.
- **History is bounded by the member's window.** Reading, searching, marking read, renewing a file link and editing all apply `JoinedAt` and `LeftAt` inside the query.
- **Roles.** A group has one owner, any number of admins and plain members. The owner can do everything; an admin can add people, remove plain members, rename and delete messages; a plain member can only send and leave. A direct chat has no admins.
- **Notifications** are scoped by the caller's id in the same statement as the read or update. Someone else's id is a 404, is ignored, or gets the same 400 as an unknown id.
- **Presence** is visible only to contacts, never to the user themselves, and the push is addressed to user ids so a non-contact receives nothing.
- **Mute** is one `UPDATE` scoped to the caller's own membership row; nobody else can see or change it.

## Concurrency

Races are settled by the database, not by check-then-write.

- A direct chat is unique per pair through a partial unique index on `DirectKey`.
- A retried send is stored once through a partial unique index on `(ConversationId, SenderId, ClientMessageId)`.
- The read position moves with one conditional `UPDATE`, only forward.
- An edit and a delete are each one conditional `UPDATE`, so a delete that arrives first wins.
- Every change to a group's members runs in a transaction behind a per-conversation advisory lock, with the rules applied to the rows read after the lock was taken, so there is exactly one owner at every moment.
- Refresh rotation is one conditional `UPDATE`: of simultaneous refreshes exactly one wins and the others get a 409 and retry.

## Files

- The upload ticket checks membership, the account, and the file's declared type and size. The send re-checks the object that was really stored, because a presigned PUT cannot enforce limits itself: the key belongs to the caller, the object exists, its size is between 1 byte and the limit, and its stored content type is on the allowed list.
- The allowed list has no SVG, HTML, script or executable types. Pictures of people and groups allow only PNG, JPEG, WebP and GIF, up to 2 MB.
- A file that is not an image or a video is served with `Content-Disposition: attachment`.
- File names are cleaned (path parts and control characters removed, 255 characters at most).
- Links are signed for the reader and expire (upload links after 10 minutes, download links after 60). Renewal re-checks membership, the join window and deletion, and gives one 404 for every refusal.
- A link that leaks works until it expires. Someone who saved a link can use it for up to an hour after losing access.
- There is no virus scan, and files are served from the storage host rather than a separate origin.

## Rate limiting

Counts live in Redis, so every API instance applies one limit. A refused request is not carried out or counted.

| Policy | Counted per | Limit |
|---|---|---|
| `auth` (sign-in, registration) | client address | 10 per 60 s |
| `refresh` (refresh, sign-out) | client address | 60 per 60 s |
| `avatars` (the open picture route) | client address | 600 per 60 s, misses counted |
| `send` | person | 30 per 10 s |
| `uploads` (upload tickets, including pictures) | person | 60 per hour |
| `edit` | person | 20 per 60 s |
| `search` | person | 30 per 10 s |
| `push` | person | 20 per 60 s |

Not covered by these policies: reading, marking read, muting, deleting and presence. Typing is not counted in Redis; the hub delivers at most one start event per second per conversation per connection. When Redis cannot be reached the limits fail open: they are skipped for 5 seconds after a failure, one warning is logged per outage, and a bug in the limiter code is not hidden as an outage. Behind a proxy, the client address is the one the trusted proxy reports.

## Push endpoints and keys

The endpoint a browser registers is an address the server posts to, so it is treated as a capability and as a request the server makes on a client's behalf (SSRF).

- Only `https` addresses on the default port, with no user info, on a host in `Push:AllowedHosts` (a host matches its subdomains), are accepted. The check is made at registration and again before every push.
- Redirects are not followed.
- The endpoint is never in a URL, log line or trace, and a refusal never repeats it.
- The private key that signs pushes comes from configuration only, is checked to belong to the public key when the host starts, and is never logged or returned.
- A push carries ids and the text of the in-page alert, encrypted to the one browser it is for. The push service sees which endpoint, when and how big.
- A person who learns an endpoint cannot push to it without the private key.

## Transport and headers

- Every response carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` and `Referrer-Policy: no-referrer`.
- API responses are `Cache-Control: no-store`, except a picture that was found, which is cacheable for a year because its address changes when the picture does.
- HSTS (one year) is sent outside development, never over plain http. Behind a TLS-terminating proxy, `ForwardedHeaders:KnownNetworks` must name the proxy's network or the API cannot see that the request was https.
- CORS allows one configured origin, with credentials (SignalR's negotiate request needs them).
- Request bodies over 1 MB are refused with a 413 before they are read.
- The API sends no Content-Security-Policy, because it returns JSON; the web server that serves the frontend sets one.

## Deployed stack

- Only the reverse proxy is published (ports 80 and 443, and 443 over UDP for HTTP/3). Postgres and Redis have passwords and publish no ports. The optional tracing UI listens on the machine's loopback address only.
- API instances answer only the configured host names (`AllowedHosts`) and believe forwarded headers only from the proxy's network, for one hop.
- API containers run as an unprivileged user with a read-only filesystem, no capabilities and no new privileges.
- The proxy logs omit the `access_token` query value, in the access log and in its error lines.
- Secrets come from user-secrets locally and environment variables elsewhere, never from git. The JWT signing key must be at least 32 bytes or the host does not start.

## Logging

Logs carry ids and metadata only, never message content, search text, tokens, passwords, push endpoints or picture addresses. See [observability](../operations/observability.md).

## Known limits

- No Content-Security-Policy from the API itself, and no separate origin for files.
- Web Push has been tried against Chrome's push service only.
- A deactivated user keeps a valid access token for up to 15 minutes.
- One Postgres and one Redis, with no replication; the presence scripts assume a single Redis node.
