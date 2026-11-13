# Decision records

Architecture decision records (ADRs): the choices that shape ChatNET, why they were made, what was passed over and what they cost. Each record has a status, the context that forced a decision, the decision, the alternatives considered and the consequences.

Records are **Accepted** unless marked **Superseded**. Where a choice replaced an earlier one, the record says what it replaced or what replaced it. This is a curated set of the decisions a reader is most likely to ask about, not a log of every small choice.

## Structure

| Record | Summary |
|---|---|
| [0001: Vertical slices with MediatR](0001-vertical-slices-with-mediatr.md) | Each feature owns its endpoint, command, validator, handler and data access in one folder. |
| [0002: One conversation model for direct and group chats](0002-one-conversation-model.md) | Direct and group chats share one table and one membership table, told apart by a type. |
| [0003: Direct chats are unique by a database index](0003-direct-chat-uniqueness-in-the-database.md) | A sorted `DirectKey` with a unique partial index guarantees one direct chat per pair of people. |
| [0004: PostgreSQL holds the truth, Redis holds what can be rebuilt](0004-postgres-durable-redis-ephemeral.md) | Messages and all durable data are in Postgres; Redis holds the backplane, presence and counters. |
| [0005: Soft deletion and times fixed by the server](0005-soft-deletion.md) | A deleted message keeps its row with the text removed; accounts are deactivated, never removed. |

## API and clients

| Record | Summary |
|---|---|
| [0006: REST writes, SignalR pushes](0006-rest-writes-signalr-pushes.md) | Clients send and load over REST; the hub only pushes events. |
| [0007: One error format: ProblemDetails](0007-problemdetails-error-contract.md) | Every error is application/problem+json from one writer; domain failures are exceptions. |
| [0008: A browser store that merges by id](0008-merge-by-id-browser-store.md) | REST responses and pushes feed one store through actions that merge by id, so order does not matter. |
| [0009: Page history by message id with keyset comparison](0009-keyset-pagination-by-message-id.md) | History pages are keyed on a message id, resolved to its (CreatedAt, Id) position inside the caller's window. |
| [0010: Catch up after a reconnect by reading what the tab missed](0010-client-driven-catch-up.md) | After a reconnect each loaded conversation fetches its latest page and merges only what joins up with what it holds. |

## Authentication

| Record | Summary |
|---|---|
| [0011: A short access token and a rotating refresh token](0011-access-and-refresh-tokens.md) | A 15-minute JWT kept in memory, plus a 14-day refresh token in an httpOnly cookie that the server can revoke. |
| [0012: Refresh tokens rotate in families and reuse is detected](0012-refresh-token-rotation-and-reuse-detection.md) | Each refresh retires the token and issues its successor; a retired token reused after a grace period revokes the family. |
| [0035: Password reset links are random values stored hashed, not Identity tokens](0035-hashed-password-reset-tokens.md) | A reset link is 32 random bytes whose SHA-256 is stored in its own table, used up by one conditional UPDATE, so no key ring has to be shared between instances. |

## Real-time

| Record | Summary |
|---|---|
| [0013: SignalR with a Redis backplane and one group per conversation](0013-signalr-redis-backplane-and-groups.md) | Every connection joins the group of each of its conversations, and Redis connects the API instances. |
| [0014: A transactional outbox with a SKIP LOCKED relay](0014-transactional-outbox.md) | A push is recorded in the same transaction as the change, so a committed change is always delivered at least once. |
| [0015: Presence in Redis, changed only by Lua scripts](0015-presence-in-redis.md) | Who is online lives in two sorted sets and a marker per user; servers renew their own connections and clients send no heartbeats. |
| [0016: Taking a removed member's connections out of the group on every instance](0016-evicting-connections-across-instances.md) | A removal publishes on a Redis channel and every instance removes the connections it holds. |

## Messaging

| Record | Summary |
|---|---|
| [0017: A retried send is stored once, by client message id](0017-idempotent-send.md) | The id the browser invents is stored under a partial unique index; a repeat answers 200 with the stored message. |
| [0018: The read position is a timestamp moved forward by one UPDATE](0018-read-position.md) | One timestamp per member, set from a message's own time and only ever moved forward by a conditional UPDATE. |
| [0019: Edit and delete are single conditional UPDATEs](0019-edit-and-delete-as-conditional-updates.md) | The statement itself checks the window and the deleted state, so races are settled by Postgres. |
| [0020: Mentions are read by the server and saved with their cause](0020-server-parsed-mentions.md) | Superseded: the sender's browser names the people as ids, and the rows are saved in the same transaction as the message. |
| [0021: Group changes run behind a per-conversation advisory lock](0021-group-changes-behind-an-advisory-lock.md) | Every membership change applies its rules to rows read after taking a lock on the conversation. |
| [0022: Full-text search inside Postgres, in two phases](0022-postgres-full-text-search.md) | Superseded: the server cannot read messages and no longer searches them. It was a GIN index on the conversation id and the message words, searched per conversation with a bounded recent phase first. |

## Files

| Record | Summary |
|---|---|
| [0023: S3-compatible object storage](0023-s3-compatible-object-storage.md) | Files live in a bucket reached through the S3 API: designed for Cloudflare R2, with RustFS locally and in tests. |
| [0024: Uploads go straight to storage and are checked when the message is sent](0024-direct-uploads-checked-at-send.md) | A ticket gives a presigned PUT; the send inspects what was really stored and copies it only if it is unchanged. Accepted, in part superseded: the type and name checks went when files were encrypted. |
| [0025: Pictures at immutable addresses served by the API](0025-immutable-picture-addresses.md) | A picture is served from the private bucket without a token at an address that never serves two pictures, cached for a year. |

## Security

| Record | Summary |
|---|---|
| [0026: Rate limits counted in Redis, failing open](0026-redis-rate-limits-fail-open.md) | A sliding-window log decided by one Lua script gives every instance one limit; when Redis is down the limits are skipped. |
| [0027: Web Push written on System.Security.Cryptography](0027-web-push-implementation.md) | The RFC 8291 encryption and RFC 8292 VAPID signing are implemented in the repository, with no package. |
| [0028: A push endpoint is a capability checked against an allowlist](0028-push-endpoint-allowlist.md) | Only https addresses of listed push services are accepted, checked again before every push, with no redirects followed. |
| [0029: Forwarded headers are believed only from known networks](0029-forwarded-headers-from-known-networks.md) | `X-Forwarded-For` and `-Proto` are applied first in the pipeline, for one hop, only from configured networks. |
| [0036: Asking for a reset link does the same work and gives the same answer for every address](0036-no-account-enumeration-in-password-reset.md) | The request only writes one outbox row, so an address with no account, a deactivated one and a request over a limit are indistinguishable from a real one by answer or by time. |

## Operations

| Record | Summary |
|---|---|
| [0030: One Compose file for one machine, with Caddy in front](0030-single-machine-compose-with-caddy.md) | Caddy, the API instances, a migration job, Postgres and Redis in one Compose file; files in external object storage. |
| [0031: Images for x86-64 and ARM64 from the same Dockerfiles](0031-multi-architecture-images.md) | The CPU is taken from the platform being built; compile steps always run on the build machine. |
| [0032: Hand-written SQL where LINQ cannot express an efficient query, guarded by plan tests](0032-raw-sql-and-query-cost-tests.md) | A few queries are written in SQL, with parameters and a test that asks Postgres how many rows the real query reads. |
| [0033: Integration tests run against real services](0033-integration-tests-against-real-services.md) | The real API runs in-process against real Postgres, Redis and object storage in throwaway containers. |
| [0034: Logging and tracing record ids, never content](0034-logging-and-tracing-without-content.md) | Structured logs and OpenTelemetry traces carry ids and metadata; message text, search text, tokens and addresses stay out. |

## Encryption

| Record | Summary |
|---|---|
| [0037: Conversation keys are made by members and checked by the server only for completeness](0037-conversation-keys-made-by-members.md) | A member's browser makes and seals the key of each epoch; the server stores it and refuses an epoch that does not cover exactly the people in the conversation. |
| [0038: Messages are sealed in the browser, and the server stores a blob it checks only for shape and epoch](0038-messages-stored-as-ciphertext.md) | The text is sealed and signed by the sender; the server checks the shape and the epoch, stores it as `Ciphertext`, and takes names as ids. |
| [0039: Files are encrypted in the browser, and the server enforces size and count only](0039-files-encrypted-in-the-browser.md) | Each file is encrypted in pieces under a key made for it, the key travels inside the sealed message, and the server limits size and count because it cannot see what a file is. |
| [0040: Search runs in the browser over a local store kept current by a changes feed](0040-search-in-the-browser-over-a-changes-feed.md) | Each conversation numbers its changes under a row lock and serves them as a feed; the browser keeps the messages it opened in IndexedDB and scans them. |
