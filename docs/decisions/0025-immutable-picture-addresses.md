# Pictures at immutable addresses served by the API

Status: Accepted

## Context

An `<img>` cannot send a token, so pictures cannot use signed or bearer-protected links without defeating caching.

## Decision

A picture is served by the API from the private bucket at `/api/avatars/users/{ownerId}/{fileId}` or `/api/avatars/groups/...`, with no token and `Cache-Control: public, max-age=31536000, immutable`. A changed picture gets a new random file name, so an address never serves two pictures and the old address answers 404. The ids are random, so addresses cannot be guessed or listed. The routes have their own rate limit per client address, with misses counted. The browser crops and shrinks the picture (at most 256 px, WebP else JPEG) before uploading.

## Alternatives considered

- A second, public bucket: more setup and a public base URL setting.
- Signed links in every response: a new URL each time defeats caching.

## Consequences

- Anyone who has an address can load that picture without signing in.
- A first load of each picture passes through the API.
- Picture addresses are relative, so the page and the API must share an origin.

## Related

- [Profiles and pictures](../features/profiles-and-pictures.md)
