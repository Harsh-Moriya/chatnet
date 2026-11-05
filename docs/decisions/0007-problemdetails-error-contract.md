# One error format: ProblemDetails

Status: Accepted

## Context

Clients should handle one error shape whoever produced the error: a handler, the framework or the rate limiter.

## Decision

Every error response is `application/problem+json`, written by `IProblemDetailsService`, with `status`, `title`, `detail`, `type` and `traceId`. Handlers throw domain exceptions (`NotFound`, `Unauthorized`, `Forbidden`, `Conflict`, `TooManyAttempts`) that one exception handler maps to statuses. Validation failures are 400 with the messages joined in `detail` and a per-field `errors` map. An unreadable request is a 400 with a deliberately generic detail, and the framework is told to throw on a bad request in every environment so Development and Production answer alike. Success responses have no envelope.

## Alternatives considered

- Hand-written JSON at each place that fails.
- A `Result<T>` type through every layer: more wrapper types for the same mapping.

## Consequences

- Clients switch on `status` and never parse text.
- Expected failures cost an exception, which is acceptable at this request rate.

## Related

- [Errors](../api/errors.md)
- [Request flow](../architecture/request-flow.md)
