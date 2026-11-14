# Security model

What protects the system, how each protection works, and where its limits are. The limits are stated on purpose: a protection described without its limit is a claim, not a design.

## Identity and sessions

| Concern | How it is handled | Limit |
|---|---|---|
| Who is calling | A JWT access token, signed HS256, validated for signature, issuer, audience and lifetime. It lives 15 minutes and is kept in memory only. Claims: `sub`, `email`, `unique_name`, `display_name`, `jti`. | A token cannot be taken back. A deactivated user keeps what they hold for at most 15 minutes and is not renewed; sending is refused for them at once. An expired token is rejected after a 30-second tolerance. |
| Staying signed in | A refresh token: 32 random bytes in the `chatnet_refresh` cookie, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/api/auth`. Only its SHA-256 hash is stored. Each refresh retires the token and issues the next in the same family. A retired token presented again after 30 seconds revokes the whole family. | A thief who refreshes first and an owner who refreshes second are both signed out, and nothing says which was which. A copied token that is never used again goes unnoticed until it expires (14 days). A refresh whose reply is lost leaves the browser with a retired token; presented after the grace period it looks like theft and the session ends. |
| Forgotten passwords | A single-use link by email. The token is 32 random bytes and only its SHA-256 hash is stored. It works for 30 minutes and once, and is in the URL fragment, so it is in no request line, proxy log or `Referer`. Asking answers 202 for every address and does the same work for each, and the email goes to the stored address with no name in it. A reset runs in one transaction under a row lock: it changes the password and the security stamp, clears the lockout, revokes every refresh token, removes the push registrations and voids the other links. | Whoever can read the mailbox of an account can reset it, because the address is not confirmed at registration. An access token issued before a reset works for up to 15 minutes more. Someone can use up the three emails an hour of an address they do not own. |
| Cross-site requests to refresh and sign-out | `SameSite=Strict` keeps the cookie off cross-site requests, and both endpoints also refuse a request without `X-Requested-With: ChatNET`. | |
| Passwords | Identity's salted PBKDF2 hashing, never logged. At least 8 characters with a digit, a lowercase and an uppercase letter. Email length is capped at 254 and password length at 128, so a request cannot ask the server to hash an enormous value. | |
| Guessing passwords | A per-address rate limit on sign-in (10 per minute) and account lockout (5 failures, 15 minutes). | While Redis cannot be reached the rate limit is not applied and only the lockout, stored in Postgres, protects a password. |
| User enumeration | An unknown email and a wrong password give the same 401 text, and an unknown email costs the same password check as a wrong one. | Registration reveals which emails exist (a 409). |
| Account creation | Registration takes a Postgres advisory lock on the email, so simultaneous registrations for one email run one at a time. A unique index on the normalised email is the final guard. | |
| Tokens in the browser | The access token lives in memory; the refresh token is in a cookie scripts cannot read; no token is written to browser storage. (The keys of a device and the copy of messages kept for search are in IndexedDB: see Keys and devices.) | A script injected into the page can use the access token while the page is open (15 minutes at most). It cannot copy the long-lived credential. |
| Tokens in URLs | Accepted from `?access_token=` only on `/hubs/chat`. | Query strings can appear in proxy logs; the production proxy's logs are configured to leave the token out. |

## Authorization

Every endpoint requires authentication unless it is marked anonymous (sign-in, registration, asking for and using a password reset link, refresh, sign-out, `/health`, and the two picture routes). The set of anonymous endpoints is pinned by a test.

- **Identity comes from the token.** A request body never says who the caller is.
- **Membership is checked in the handler or the hub**, never trusted from the client. A conversation that does not exist and one the caller is not in get the identical 403, so ids reveal nothing.
- **History is bounded by the member's window.** Reading, marking read, renewing a file link and editing all apply `JoinedAt` and `LeftAt` inside the query.
- **Roles.** A group has one owner, any number of admins and plain members. The owner can do everything; an admin can add people, remove plain members, rename and delete messages; a plain member can only send and leave. A direct chat has no admins.
- **Notifications** are scoped by the caller's id in the same statement as the read or update. Someone else's id is a 404, is ignored, or gets the same 400 as an unknown id.
- **Presence** is visible only to contacts, never to the user themselves, and the push is addressed to user ids so a non-contact receives nothing.
- **Mute** is one `UPDATE` scoped to the caller's own membership row; nobody else can see or change it.

## Concurrency

Races are settled by the database, not by check-then-write.

- A direct chat is unique per pair through a partial unique index on `DirectKey`.
- A retried send is stored once through a unique index on `(ConversationId, SenderId, ClientMessageId)`.
- A send is stored only under the epoch the conversation is in and not marked for replacement: one conditional `UPDATE` of the conversation row, in the transaction of the insert, decides it, and it takes the row lock that every change of the conversation's keys takes, so a removal and a send racing each other are settled by Postgres and a message is never stored under an epoch that a removal had already marked.
- An edit first claims the conversation with the same conditional `UPDATE` a send uses (the epoch it was sealed under, not marked for replacement, the next change number, under the row lock every change of the conversation's keys takes), then updates the message only if its edit count is the one before the new count. Two edits made from one version store one, and an edit cannot land between a removal's marking and its commit.
- The read position moves with one conditional `UPDATE`, only forward.
- An edit and a delete are each one conditional `UPDATE`, so a delete that arrives first wins.
- Every change to a group's members runs in a transaction behind a per-conversation advisory lock, with the rules applied to the rows read after the lock was taken, so there is exactly one owner at every moment.
- Refresh rotation is one conditional `UPDATE`: of simultaneous refreshes exactly one wins and the others get a 409 and retry.
- Using a password reset link is one conditional `UPDATE` that marks it used and returns the person, so of simultaneous requests with one link exactly one succeeds.
- Setting up keys stores the bundle and the backup in one transaction, and the primary key of the bundle decides two set-ups made together.
- Replacing a backup is one conditional `UPDATE` on the version, so of devices changing the passphrase together exactly one wins.
- Adding a device and opening a link request count and insert behind a per-person advisory lock, so requests made together never exceed the limit.
- Handing over the sealed secrets of a link request is one statement that reads and clears them, so of simultaneous polls exactly one gets them.
- A password reset locks the person's row, and renewing a refresh token and starting a session take a share lock on it, so a session being made at the moment of a reset is either revoked by it or refused.

## Files

- A file is encrypted in the sender's browser, in chunks, under a key made for it, and the key travels only inside the sealed message. The bucket holds ciphertext; the server and the storage provider can read neither the content nor the name or the type.
- Because the server cannot see what a file is, it enforces size and count only. The upload ticket checks membership, the account and the size of the encrypted file. The send re-checks the object that was really stored, because a presigned PUT cannot enforce limits itself: the key belongs to the caller, the object exists, and its size is between the smallest and the largest an encrypted file can have.
- Every upload is signed for the content type `application/octet-stream`, so the storage never learns or serves a file as anything else.
- What a file is called and what type it is are claims by the sender, made inside the sealed message. A reader cleans the name (path parts, control characters and invisible formatting characters removed, 255 characters at most), gives a picture or a video its type only when it is one of PNG, JPEG, GIF, WebP, MP4 or WebM, and saves everything else as opaque bytes, so a file that says it is a web page or an SVG is never opened as one in the app's origin.
- A file is tied to its entry in the message by position and by size, and its chunks are authenticated, so a file swapped in the bucket, cut short or paired with another entry is refused, and nothing of it is drawn or saved.
- Pictures of people and groups are not encrypted: they allow only PNG, JPEG, WebP and GIF, up to 2 MiB, and are served by the API.
- Links are signed for the reader and expire (upload links after 10 minutes, download links after 60). Renewal re-checks membership, the join window and deletion, and gives one 404 for every refusal.
- A link that leaks works until it expires, and gives bytes that cannot be opened without the key in the message. Someone who saved a link can fetch the encrypted file for up to an hour after losing access.
- There is no virus scan, and files are served from the storage host rather than a separate origin.

## Keys and devices

The server stores what the browsers encrypt and relays what they seal for each other; it opens none of it. The formats are in [encryption formats](encryption-format.md) and the behaviour in [encryption keys and devices](../features/encryption-keys.md).

| Concern | How it is handled | Limit |
|---|---|---|
| Private keys | Made in the browser. The server holds the public bundles and a backup of the private keys encrypted under a key stretched from a passphrase (PBKDF2-HMAC-SHA256, 600,000 rounds). The passphrase is never sent | Whoever obtains a backup can try passphrases offline, so it is returned only to its owner (no route takes an id), and the passphrase is the protection. A guess costs 600,000 rounds, about 0.15 s of one core (measured in Chrome and in Node), so a passphrase of a few common words falls to an attacker with many cores or graphics cards, and five or more random words or a long random string does not. The minimum of 12 characters is a floor on length and says nothing about how guessable a passphrase is. The keys on a device are kept in IndexedDB in a form that can be exported, so a script injected into the page can read them while it is open |
| Public keys | Each bundle is signed by the identity key of its owner and bound to their user id. The server checks the size, version and generation | The server cannot verify the signature; a client that opens a bundle does, against the identity key it remembers |
| Backup and link payloads | Accepted only in the right shape and size (the backup up to 64 KiB, a sealed payload up to 64 KiB). Replacing a backup names the version it was made from, in one conditional `UPDATE` | The server cannot tell a wrong backup from a right one |
| Setting up and devices | Setting up stores the bundle and the backup in one transaction and a second set-up is a `409`. Adding a device counts and inserts under a per-person lock, so devices added together never exceed the limit | |
| Linking a device | The one-time public key and a 50-bit code derived from it are stored. The signed-in device checks the key the server returned against the code the person typed. The sealed secrets are handed over once by a statement that also clears them, expire with the request (10 minutes) and are returned only to the same person | A request is not bound to a physical device: whoever the person approves gets the keys. A server that swapped the key would have to find another whose hash starts with the same 50 bits: about 10^15 attempts, each a key generation and a hash. One core makes about 8,000 a second with a general-purpose library, so that is out of reach of one machine, but a search built for the purpose on a great deal of hardware is not slow enough to rule out inside the 10 minutes a request stays open. The code protects against a server that swaps keys without such resources. The QR code carries the whole key and has no search. The web app shows the QR code for another app to scan and has no scanner: a person types the code |
| Sessions and devices | A session is tied to a device by the `X-Device-Id` header on sign-in and renewal, only if the device is the person's and not removed. Removing a device revokes the refresh tokens tied to it, and renewing a session tied to a removed device ends it, so a renewal that overlapped the removal does not keep the session alive | Removing a device does not change the keys it held |
| A lost device | A new generation of the account key, stored with its backup, the removal of the device and the marking of the person's conversations, in one transaction under a lock on the person's keys. Every path that seals a conversation key to someone reads their generation under the shared side of that lock in the transaction that saves the epoch | The device keeps the identity key and what it already read, including the plain-text copy of messages in its browser, which nothing deletes until someone signs in on it again. Its refresh tokens end at once, but an access token it already holds works until it expires (15 minutes at most): in that time an online device can still call the API as the person and, holding the identity key, sign and store the next key of a conversation that is due, a key it would then know. The other devices need the passphrase once to get the new private key |
| Starting over | A new identity and generation, a new backup, every device removed and every conversation marked, after the account password is asked again with the lock rules of signing in. A wrong password is a `403`, never a `401`, so the browser does not take it for an expired session | Everything sealed so far is lost to the person, and everyone who knows them must accept a new identity key. Whoever has the password and an unattended browser can do this, which is why the password is asked |
| Control of keys by a session | Reading and replacing the backup, making a generation and removing a device ask only for a valid session of the person; starting over also asks for the password | A thief of a session can replace the backup with one the person cannot open, or publish an account key of their own. Other people check a bundle against the identity key they remember, so a bundle made without it is refused and nobody can read as a result, but the keys of the person stop working until they start over |
| Logs | Ids and sizes only: never a key, a bundle, a backup, a sealed payload, a code, a password or a device name | |

### Keys of conversations

The key of each period of a conversation is made in a member's browser and sealed once for each member. The behaviour is on the [conversation keys](../features/conversation-keys.md) page.

| Concern | How it is handled | Limit |
|---|---|---|
| Who can open a key | Only the member an envelope is sealed for, with the private key of the generation named in it. The server stores envelopes and returns each member only their own | The server can withhold an envelope or serve an old epoch |
| Who is included | Every epoch must cover exactly the people in the conversation, each at their newest key generation. This is checked under the conversation's advisory lock, together with the change that needs it, so joining and the epoch for the joiners are saved together or not at all | The server cannot tell a real envelope from random bytes of the right size; the members check |
| Forged or substituted epochs | A member checks the creator's signature over the record, that the creator and the member are in it, that the identity key is the one first seen for the creator, that the key matches the signed commitment and, for the newest epoch, that the people are the ones in the conversation | The first sight of a person's identity key is trusted. A changed key is refused until accepted |
| Someone who left | They keep the envelopes of the epochs they were in and receive none after. The epoch in force is marked due for replacement in the same transaction as their leaving | Until a member makes the next epoch, the one in force is still the one the person who left holds |
| Joining | A person who joins is given an envelope for the epoch made at their joining and none for earlier ones | |
| Who can make a key | Any current member. Others, and people who left, are refused with the same `403` as any conversation they are not in | A member can replace the key, as can one who is unhappy with it, but at most once a day unless a removal or a leave marked it as due. A member who means harm can store an epoch that nobody can open, because the server cannot tell it from a real one: the others find they cannot open it, and cannot replace it for up to a day. Removing that member marks the epoch due and allows an immediate replacement |
| Stale and concurrent epochs | The next number is required, and the primary key of the table decides any tie. The later request gets a `409` with a code the client acts on | |
| Replacing on a timer | A browser replaces an epoch that is seven days old, whoever opens or sends first; a message is never refused for want of a replacement | The server does not enforce the week, so a client that never replaces keys is not stopped |
| Changed identity | A person whose identity key is not the remembered one is refused until the person looking compares the safety number and accepts. The mark that a person was verified belongs to one key and is gone when another is accepted | The first sight of a person's identity key is trusted, and the marks are kept in one browser |
| Logs | Ids and sizes only: never an envelope, a commitment or a signature | |

### Messages

The text of a message is sealed in the sender's browser under the key of the conversation's epoch and signed with the sender's identity key. The format is in [encryption formats](encryption-format.md).

| Concern | How it is handled | Limit |
|---|---|---|
| Who can read a message | The members' browsers, with the epoch key they hold. The database stores `Ciphertext` and the server has no key that opens it | A person who holds the epoch key can read every message of the epoch, and the server serves the JavaScript, so a malicious server could change the code that seals and opens |
| What the server checks | The shape of the blob: version and suite bytes, 98 to 32,768 bytes, and an epoch of at least 1 that is the one stored with the message. It cannot open or verify a blob; readers verify the signature | A member who is the sender can store a blob that nobody can open; it is shown as one that cannot be opened |
| Moving or replaying a message | The conversation, sender, client message id and edit count are bound into the encryption and the signature, so a blob moved to another conversation, shown as sent by someone else or replaced by an older version fails to open | |
| Under which key | A send or an edit under an epoch other than the current one is a `409` (`epoch-stale`), and under one marked for replacement a `409` (`epoch-due`), so what is sent after a removal is never sealed under a key the removed person holds | |
| Editing | The new blob is for the next edit count, and the server requires exactly that, so a stale edit is a `409` (`edit-count-stale`) | |
| Mentions | The sender's browser says which people the text names, as ids. The server keeps the active members other than the sender | A sender can name a member the text does not mention; it notifies them |
| Alerts | Web Push carries who and where, never text. A page that is open shows the text it opened | An alert the page raises while it is open in another tab carries the start of the message, so that text reaches the notification center and the lock screen of the operating system |
| Changes feed | `GET /api/conversations/{id}/changes` applies the history's rules: only a member, and only messages created inside the member's window; anyone else is a `403`. A point above the conversation's number is a `409`, so a client is never told it is up to date by a server that is older than it | The feed returns sealed blobs and numbers, so it shows the server's own metadata and nothing more |
| Search | Runs in the browser over a copy of the messages the device opened, kept in IndexedDB under the person's user id and removed when they sign out. Nothing about a search reaches the server | The copy is plain text, in the same database as the keys of the device: whoever can read the browser profile, or a script in the page, can read it. A session that only expires keeps it, as it keeps the keys |
| What stays visible | Who writes to whom and when, the size of a blob (padded to 256 bytes), the number of files, and the size of each file (a little more than the file) | A person who left, or who joined later, still sees the key epoch, whether it is due and the change counter of the conversation: these are not limited to their window |

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
| `push` | person | 20 per 60 s |
| `keys` (keys, backup, devices, link requests, key lookup, conversation key epochs) | person | 60 per 60 s |
| `forgot-password` (asking for a reset link) | client address | 5 per 900 s |
| `reset-password` (using a link) | client address | 10 per 900 s |
| `password-reset-email` (the emails sent for a link) | email address | 3 per 3,600 s |

Not covered by these policies: reading, marking read, muting, deleting and presence. Typing is not counted in Redis; the hub delivers at most one start event per second per conversation per connection. When Redis cannot be reached the limits fail open: they are skipped for 5 seconds after a failure, one warning is logged per outage, and a bug in the limiter code is not hidden as an outage. Behind a proxy, the client address is the one the trusted proxy reports.

## Push endpoints and keys

The endpoint a browser registers is an address the server posts to, so it is treated as a capability and as a request the server makes on a client's behalf (SSRF).

- Only `https` addresses on the default port, with no user info, on a host in `Push:AllowedHosts` (a host matches its subdomains), are accepted. The check is made at registration and again before every push.
- Redirects are not followed.
- The endpoint is never in a URL, log line or trace, and a refusal never repeats it.
- The private key that signs pushes comes from configuration only, is checked to belong to the public key when the host starts, and is never logged or returned.
- A push carries ids, who it is from and what kind it is (a new message, a file, a mention, an invitation), encrypted to the one browser it is for. It never carries the text of a message. The push service sees which endpoint, when and how big.
- A person who learns an endpoint cannot push to it without the private key.

## Transport and headers

- Every response carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` and `Referrer-Policy: no-referrer`.
- API responses are `Cache-Control: no-store`, except a picture that was found, which is cacheable for a year because its address changes when the picture does.
- HSTS (one year) is sent outside development, never over plain http. Behind a TLS-terminating proxy, `ForwardedHeaders:KnownNetworks` must name the proxy's network or the API cannot see that the request was https.
- CORS allows one configured origin, with credentials (SignalR's negotiate request needs them).
- Request bodies over 1 MiB are refused with a 413 before they are read.
- The API sends no Content-Security-Policy, because it returns JSON; the web server that serves the frontend sets one.

## Production configuration

- Only the reverse proxy is published (ports 80 and 443, and 443 over UDP for HTTP/3). Postgres and Redis have passwords and publish no ports. The optional tracing UI listens on the machine's loopback address only.
- API instances answer only the configured host names (`AllowedHosts`) and believe forwarded headers only from the proxy's network, for one hop.
- API containers run as an unprivileged user with a read-only filesystem, no capabilities and no new privileges.
- The proxy logs omit the `access_token` query value, in the access log and in its error lines.
- Secrets come from user-secrets locally and environment variables elsewhere, never from git. The JWT signing key must be at least 32 bytes or the host does not start.

## Logging

Logs carry ids and metadata only, never message text, keys, backups, sealed payloads, tokens, passwords, push endpoints or picture addresses. See [observability](../operations/observability.md).

## Known limits

- No Content-Security-Policy from the API itself, and no separate origin for files.
- Web Push has been tried against Chrome's push service only.
- A deactivated user, and a device that was removed, keep a valid access token for up to 15 minutes.
- The Content-Security-Policy the web server sends restricts framing, forms, the base address and plugins but not scripts, so it does not stop a script injected into the page, which could read the keys of the device. A policy that restricts scripts must still allow `data:` and `blob:` images and `blob:` media (the QR code and decrypted files) and connections to the storage host.
- The encryption needs X25519 and Ed25519 in the Web Crypto API, IndexedDB and a secure context. It has been run in Chrome; the published support of Firefox, Safari and Edge covers both algorithms, and the app has not been run in them.
- One Postgres and one Redis, with no replication; the presence scripts assume a single Redis node.
