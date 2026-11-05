# PostgreSQL holds the truth, Redis holds what can be rebuilt

Status: Accepted

## Context

Messages must not be lost, and several things (who is online, rate-limit counts, group delivery between instances) are only meaningful while the system runs.

## Decision

PostgreSQL holds all durable data, messages included, with transactions, constraints and indexes such as `(ConversationId, CreatedAt DESC)` for the dominant "latest messages" query. Redis holds only data that can be rebuilt: the SignalR backplane, presence entries, and rate-limit counters. Messages and other durable rows are soft-deleted (text nulled, row kept) so foreign keys stay valid and the timeline has no gaps.

## Alternatives considered

- Redis or a document store for messages: weaker constraints and durability for the data that matters most.
- Presence rows in Postgres: a write per heartbeat.

## Consequences

- Losing Redis loses presence, counters and in-flight pushes, but no message.
- Everything that must survive is written in a Postgres transaction.

## Related

- [Data model](../architecture/data-model.md)
- [Real-time design](../architecture/real-time.md)
