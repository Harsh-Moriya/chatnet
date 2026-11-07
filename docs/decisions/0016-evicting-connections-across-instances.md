# Taking a removed member's connections out of the group on every instance

Status: Accepted

## Context

Joining a group is something a connection does once, so a removed person kept hearing the conversation, and typing into it, until their next reconnect. SignalR cannot list a user's connections, and a server can act only on its own.

## Decision

Each instance keeps a registry of its own connections by user. The outbox job that follows a leave or removal publishes on the Redis channel `chatnet:connections:evict`, and every instance, the publisher included, removes the connections it holds from the conversation's group and from the typing check. `JoinConversation` asks for membership again after it joins, and a new connection reads its conversations again after its joins, so a join that a removal slipped in front of leaves the connection out.

## Alternatives considered

- A registry of every connection in Redis: it would have to be kept alive and cleaned up like presence data.
- Closing the connection: the person reconnects, a worse experience.
- A typing check that asks the database: a query per typing event.

## Consequences

- Removal takes effect at once on every instance.
- Going through the outbox means a failed publish is retried.

## Related

- [Group management](../features/group-management.md)
