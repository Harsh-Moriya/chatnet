# A browser store that merges by id

Status: Accepted

## Context

A push can land while a request is in flight, and a request can return something a push already delivered. Messages also change after they are sent, and the outbox delivers at least once.

## Decision

Chat state lives in one Zustand store. REST responses and SignalR events both enter through actions that merge by id. Of two copies of one message the store keeps the later: a deleted copy beats any other, otherwise the higher edit count wins. An unconfirmed message is matched to its confirmed copy by an id the browser invents (`clientMessageId`), never by text. An action that changes nothing returns the same state object so nothing re-renders. The hub wiring is plain TypeScript with no React, so it is tested with a fake hub.

## Alternatives considered

- Components reading the TanStack cache with SignalR patching it: two sources of truth in one structure.
- A store that replaces on load: a late response would erase a newer push.

## Consequences

- Duplicate and late deliveries are harmless.
- Every new kind of change needs a rule for which copy wins.

## Related

- [Real-time design](../architecture/real-time.md)
- [Real-time events](../api/realtime-events.md)
