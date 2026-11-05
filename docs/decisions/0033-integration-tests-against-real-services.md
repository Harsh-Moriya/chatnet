# Integration tests run against real services

Status: Accepted

## Context

Wiring, SQL, migrations and concurrency bugs are invisible to mocks.

## Decision

Backend tests use `WebApplicationFactory` with Testcontainers for Postgres, Redis and the object store, with no mocking of the project's own infrastructure. One shared host is used and tests isolate themselves with unique data. Configuration is passed through environment variables, and the host refuses to run unless it is using the test containers, so a test cannot touch a development database. Races are tested deterministically: a repository double holds two requests at a barrier after both found nothing. Hub tests use a real SignalR client over WebSockets. Frontend logic is plain TypeScript tested with Vitest and a fake hub. CI runs both suites, the frontend lint and build, and dependency vulnerability checks.

## Alternatives considered

- In-memory fakes, SQLite or mocked repositories.
- Cleaning data between tests.
- Running tests against R2 itself.

## Consequences

- Tests need Docker.
- The suite takes minutes rather than seconds.

## Related

- [Running locally](../operations/running-locally.md)
