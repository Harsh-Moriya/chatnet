# Hand-written SQL where LINQ cannot express an efficient query, guarded by plan tests

Status: Accepted

## Context

EF Core translated the conversation list's "latest message" lookup to a window function over every message: with 600,000 messages it read about 600,000 rows and took about 450 to 560 ms.

## Decision

Hand-written SQL is allowed inside a repository only when LINQ cannot express an efficient query: only constants are concatenated, every value is a parameter, a comment says why, and a test covers it. The conversation list uses a `LEFT JOIN LATERAL` per conversation and reads about 40 rows in about 1 ms. Performance is guarded by tests that run `EXPLAIN ANALYZE` on the real query and assert how many rows it read, not how long it took: row counts depend only on the query's shape, so the test is deterministic. The same technique guards history, unread counts, the notification list, search and the outbox queue. A constant count of SQL queries per request guards against N+1.

## Alternatives considered

- Leaving the query slower with every message.
- A `LastMessageId` column on conversations: removes the join but is a schema change, and every send and delete must keep it correct.
- Banning raw SQL.
- Asserting on elapsed time: flaky.

## Consequences

- A change that makes a query scan far more than it returns fails the build.
- Raw SQL depends on table and column names; tests catch a mismatch.

## Related

- [Conversations](../features/conversations.md)
- [Data model](../architecture/data-model.md)
