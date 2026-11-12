# Soft deletion and times fixed by the server

Status: Accepted

## Context

Hard deletes orphan foreign keys, leave gaps in a conversation and break read positions that point at message times.

## Decision

A deleted message keeps its row: `Content` becomes null, `IsDeleted`, `DeletedAt` and `DeletedById` are set, and its files are removed from storage. Users are deactivated with `IsDeactivated`. Message times are set by the server and cut to microseconds, so they survive a round trip through Postgres unchanged.

## Alternatives considered

- Hard delete: orphans references and loses the fact that something was there.

## Consequences

- A deleted message cannot be restored: text and files are gone.
- Readers must treat `isDeleted` messages as placeholders everywhere (unread counts, previews).

## Related

- [Editing and deleting](../features/edit-and-delete.md)
