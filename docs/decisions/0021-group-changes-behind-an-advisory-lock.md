# Group changes run behind a per-conversation advisory lock

Status: Accepted

## Context

Requests that arrive together can each pass the rules against a copy read before the other commits: an owner handing the group over while removing the person it goes to, the owner leaving while handing over, or the same person added twice.

## Decision

Every change to a group's members and its name runs in a transaction behind `pg_advisory_xact_lock` on the conversation, with the rules applied to the rows read after the lock, and saves its notifications and outbox rows in the same transaction. A group has exactly one owner while anyone is in it; when the owner leaves it goes to the admin (else the member) who has been in the longest, in the same transaction.

## Alternatives considered

- A version column with retries: a schema change.
- One conditional UPDATE per rule: the rules span the actor's and the target's rows.
- Serializable isolation: aborts and retries.
- Requiring the owner to hand over before leaving: a group could stay owned by someone who is gone.

## Consequences

- There is exactly one owner at every moment.
- Removing the lock fails the race tests.

## Related

- [Group management](../features/group-management.md)
