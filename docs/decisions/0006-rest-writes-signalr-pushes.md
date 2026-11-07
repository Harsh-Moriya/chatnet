# REST writes, SignalR pushes

Status: Accepted

## Context

A chat app can send messages over its socket, or over HTTP and receive over the socket. The two have different failure behaviour.

## Decision

Sending a message is `POST /api/conversations/{id}/messages`: it writes, and only after the commit is the message pushed to the conversation's SignalR group. The hub accepts three calls (join a conversation, start and stop typing). On the browser side TanStack Query owns server data and hub events patch the Zustand store directly; a hub event never triggers a refetch.

## Alternatives considered

- Sending through a hub method: errors would not be ProblemDetails, retries and validation would be hub-specific, and a client could see a message that later rolled back.

## Consequences

- A client never sees a message that was not committed.
- A push can be lost on its own, which is why the outbox exists (see the outbox record).

## Related

- [Real-time design](../architecture/real-time.md)
- [Messaging](../features/messaging.md)
