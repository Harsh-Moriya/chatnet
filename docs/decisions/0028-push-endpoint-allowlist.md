# A push endpoint is a capability checked against an allowlist

Status: Accepted

## Context

The address a browser registers is a URL the server will post to. Without a check, a signed-in person could register an internal address and make the server call it.

## Decision

The endpoint is only ever in a request body, never in a URL, log line or trace. Only an `https` address on the default port, with no user info, whose host is on `Push:AllowedHosts` (subdomains included) is accepted, at registration and again before every push. The HTTP client does not follow redirects. A refusal never repeats the address, and the span of the call has its URL removed.

## Alternatives considered

- Any public address: server-side request forgery.
- A check of IP ranges: a name can point anywhere.
- The endpoint as a path segment or query value: it would be copied into access logs.

## Consequences

- A new push service needs a configuration change.
- A row saved under an older list is rechecked before it is used.

## Related

- [Web Push](../features/web-push.md)
- [Security model](../architecture/security-model.md)
