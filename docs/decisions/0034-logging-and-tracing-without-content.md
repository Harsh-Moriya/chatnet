# Logging and tracing record ids, never content

Status: Accepted

## Context

Logs and traces are copied to other systems and read by more people than the data itself.

## Decision

Serilog writes readable lines in Development and compact JSON elsewhere. One request line per request is written by the first middleware, with the path without its query string and the user id. A refused hub call is one Warning. EF Core's command and save failures are Warnings because callers sometimes expect them, so an Error means "unhandled". OpenTelemetry covers ASP.NET Core, SignalR, outgoing HTTP and Npgsql, plus one span per MediatR request tagged with the request type only; a processor removes query strings and push URLs from spans. Search text is never logged, only who and how many.

## Alternatives considered

- Logging request bodies or query strings for debugging convenience.
- Logging every EF Core failure as an Error.

## Consequences

- Debugging relies on ids and the `traceId` in error responses.
- Redis commands and the SignalR push are not traced.

## Related

- [Observability](../operations/observability.md)
