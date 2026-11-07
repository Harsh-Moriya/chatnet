# SignalR with a Redis backplane and one group per conversation

Status: Accepted

## Context

The sidebar shows activity from every conversation, several API instances run side by side, and a connection's groups vanish when it closes.

## Decision

The hub uses SignalR with the Redis backplane (channel prefix `chatnet`) so any instance can deliver to connections held by another. On every connect and reconnect the hub puts the connection into `conv:{id}` for each conversation the user is an active member of; `JoinConversation` covers a conversation created while the user is connected and checks membership first, with one refusal text for an unknown conversation and one that belongs to someone else. The user id comes from the token's `sub` claim, so sending to a user reaches all their tabs. Typing is authorised from the set the connection joined, kept in the connection's own state, so it costs no query.

## Alternatives considered

- Raw WebSockets: no transport fallback and no backplane.
- Groups only for the open chat: the sidebar would go silent.
- A single instance: no horizontal scaling.

## Consequences

- Group membership has to be removed explicitly when someone leaves (see the eviction record).
- Redis is on the delivery path, so its outage delays pushes until the outbox retries them.

## Related

- [Real-time design](../architecture/real-time.md)
