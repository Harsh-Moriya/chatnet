# Presence in Redis, changed only by Lua scripts

Status: Accepted

## Context

A user may have tabs on two servers; a server may die without closing its connections; a page refresh must not flash someone offline.

## Decision

Presence is held in Redis in `chatnet:presence:user:{id}` (member a connection id, score its expiry), `chatnet:presence:expiring` (member a user id, score their newest expiry) and a short-lived `closing` marker. Only Lua scripts change them, so connect, close, settle and sweep are atomic across servers. Every time that decides an order comes from Redis (`TIME` in the script) and is returned as `at`; a client keeps a report only if its `at` is not older than the one it holds. Each server renews the connections it holds every 30 s and sweeps expired users; whoever removes a user from the expiring set is the only one to announce them. Offline is announced 5 s after the last close, counted from the last close. "Online" means "their contacts were told they are online", so a snapshot always agrees with the events already sent. Only contacts are told.

## Alternatives considered

- An in-process dictionary: wrong with more than one server.
- One key per user with a TTL: cannot count connections.
- Keyspace expiry notifications: delivery is not guaranteed and they need server configuration.
- `MULTI/EXEC`: cannot branch on a value read in the same step.
- Client heartbeats: extra traffic and something a client can fake.
- A per-client debounce for the refresh case: every client would need it and none is the truth.

## Consequences

- A server that dies is noticed within the entry lifetime (90 s) by any other server's sweep.
- The scripts assume a single Redis node.
- A person with a background tab open counts as online.

## Related

- [Presence and typing](../features/presence-and-typing.md)
- [Real-time design](../architecture/real-time.md)
