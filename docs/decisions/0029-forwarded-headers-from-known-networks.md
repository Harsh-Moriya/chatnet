# Forwarded headers are believed only from known networks

Status: Accepted

## Context

Behind a proxy the API sees the proxy's address and plain http. Without the forwarded headers it would never send HSTS and every client would share one rate-limit counter. But anyone can send those headers.

## Decision

Forwarded headers are applied as the first middleware, only from the networks in `ForwardedHeaders:KnownNetworks`, for one hop, and not at all when none are configured. The framework's default trust of the loopback address is removed. A malformed network stops the start.

## Alternatives considered

- Trusting any peer: a client could choose its own address and walk past the limit.
- Trusting the proxy by one address: the Compose network's address is not fixed.
- Reading the whole chain.

## Consequences

- Per-address limits count the real client behind the proxy.
- The proxy's network must be named, or HSTS is not sent and every client shares one counter.

## Related

- [Deployment](../operations/deployment.md)
- [Configuration](../operations/configuration.md)
