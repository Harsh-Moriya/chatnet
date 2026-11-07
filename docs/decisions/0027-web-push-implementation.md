# Web Push written on System.Security.Cryptography

Status: Accepted

## Context

Browsers accept pushes encrypted with the `aes128gcm` content coding of RFC 8291 and signed with VAPID. The library that was approved first sent only the older `aesgcm` draft encoding, with no option for the current one.

## Decision

The encryption and the signed token are written on `System.Security.Cryptography`, in a small amount of code, and checked against the worked example in RFC 8291 byte for byte. Push is optional: with no keys set the feature is off, with some set or a pair that does not match the host does not start.

## Alternatives considered

- Keeping the package on the older encoding.
- Another library: none was found for .NET with the standard encoding.

## Consequences

- No dependency, and the code is small enough to read.
- The cryptography is the project's to maintain.
- Tried against Chrome and Google's push service only.

## Related

- [Web Push](../features/web-push.md)
