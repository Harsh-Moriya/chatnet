# Observability

Structured logs, distributed traces and a health check. All three are built so that **ids and metadata appear, never content**: no message text, search text, token, password, push endpoint or picture address.

## Logs

Serilog writes to the console. In Development the lines are readable (`[12:30:01 INF] Category: message`); in any other environment each line is one compact JSON object, with `Application` set to `chatnet-api`, ready for a log shipper.

### The request line

Each request writes one line when it ends:

```
HTTP GET /api/conversations responded 200 in 4.2310 ms
```

It carries the method, the path **without the query string**, the status, the time and, as a structured property, `UserId` when the caller is signed in. `/health` is written at the lowest level so probes do not fill the log. The line is written by the first middleware after forwarded headers, so its time covers everything below.

### Levels

| Level | Means |
|---|---|
| Information | normal events: request lines, connection joins, a relay pass that delivered something |
| Warning | something refused or degraded that the system handled: a refused hub call (once, with ids), a domain exception (type and message), a push that failed and will be retried, the rate limiter skipping its limit because Redis is down, EF Core command and save failures that the caller handled |
| Error | **unhandled**: a 500, with the exception and the request method and path |

Domain failures are never errors. EF Core's command and save failures are logged at Warning because callers sometimes expect them (two requests creating the same direct chat, one losing to a unique index); an unhandled one is logged at Error by the exception handler.

Framework categories (`Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`, `System.Net.Http.HttpClient`) log at Warning by default. Levels are configured under `Serilog:MinimumLevel`; see [configuration](configuration.md).

### What is never logged

- message content, search text, display names;
- access tokens, refresh tokens, passwords, signing keys;
- push endpoints and keys; picture addresses;
- the `access_token` query value, which the SignalR WebSocket request carries. The API does not log query strings; the production proxy replaces `access_token` with `REDACTED` in its access log and in its error lines.

### Outbox rows

An outbox row's `LastError` keeps the exception type and a count, never the exception text.

## Tracing

OpenTelemetry traces cover ASP.NET Core requests, SignalR, outgoing HTTP, Npgsql (SQL statements), and one span per MediatR request (named after the request type). Query strings and content are not recorded on spans.

Traces are exported over OTLP **only when** `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Locally, Jaeger runs in Compose:

1. Set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` before `dotnet run`.
2. Open `http://localhost:16686` and look for the service `chatnet-api`.

A send is one trace: the HTTP request, the handler span and the Npgsql spans. An error response's `traceId` matches the trace and the log line, which is how a bug report with a `traceId` is followed.

In the production stack, start with `--profile tracing`, set `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` in `.env.prod`, and reach Jaeger through an ssh tunnel (`ssh -L 16686:localhost:16686 <server>`): its page listens on the loopback address only.

Redis commands and the SignalR push to Redis are not traced.

## Health

`GET /health` asks Postgres and Redis (each check is cut off after 3 seconds, so a hanging dependency reads as down) and answers:

```json
{"status":"healthy","checks":{"postgres":"healthy","redis":"healthy"}}
```

or a 503 with `"status":"unhealthy"` and the failing check named. It never says why. The container health check and the proxy's upstream check use it, so Redis being down takes an instance out of the rotation.

## What to watch

- 5xx request lines and Error-level events.
- Outbox rows that are not dispatched and keep a `LastError`: they mean pushes or file removals that keep failing.
- The rate limiter's outage warning: Redis is unreachable and limits are off.
- Warnings about push services refusing a registration, which are logged with ids and the HTTP status only.
