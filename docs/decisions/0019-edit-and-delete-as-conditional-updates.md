# Edit and delete are single conditional UPDATEs

Status: Accepted

## Context

An edit can race a delete, or arrive at the end of the edit window. A limit on editing stops someone rewriting what others have since answered.

## Decision

Only the sender edits, within `Messaging:EditWindow` (15 minutes), on a message that is not deleted. The decision is one `UPDATE ... WHERE NOT IsDeleted AND CreatedAt > cutoff` that also counts the edit. The handler checks the same things first only to give the right 403, 404 or 409. The sender deletes at any time and a group owner or admin deletes anyone's. A delete keeps the row and removes the files after the commit; removing the files is an outbox job (`MessageFilesRemoved`) repeated until the bucket accepts it, which treats an already-missing file as removed and checks that the message really is deleted first.

## Alternatives considered

- No edit limit; a 24-hour limit; admins editing others' words; a version table of old texts.
- Hard delete or keeping the files.
- A periodic sweep of the bucket for files whose message is deleted: needs a full listing and nothing says when it ran.

## Consequences

- A delete cannot be undone.
- A file the storage cannot delete is retried until it works, never left in the bucket.

## Related

- [Editing and deleting](../features/edit-and-delete.md)
