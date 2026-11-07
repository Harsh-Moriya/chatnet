# Profiles and pictures

People can change their display name and give themselves, or a group they own or administer, a picture.

## Profile

- `GET /api/users/me` returns the caller's profile.
- `PATCH /api/users/me` with `{ displayName }` changes the display name: trimmed, 1 to 100 characters. The handle never changes. The same name again is a 200 that stores and says nothing.
- A change is pushed as `ProfileChanged { id, userName, displayName, avatarUrl }` to everyone who shares an active conversation with the person and to their own tabs, through the outbox. The browser patches direct chats, lists of people and the names on messages.

## Pictures

PNG, JPEG, WebP or GIF, at most 2 MB, never SVG. The flow has three calls:

1. `POST /api/users/me/avatar/uploads` (or `POST /api/conversations/{id}/avatar/uploads` for a group) with `{ contentType, sizeBytes }` returns a ticket `{ uploadKey, url, expiresAt }`: a presigned PUT under `pending/avatars/{users|groups}/{ownerId}/{guid}`.
2. The browser PUTs the file to the bucket.
3. `PUT /api/users/me/avatar` (or `PUT /api/conversations/{id}/avatar`) with `{ uploadKey }` checks the stored object (it is this owner's key, it exists, it is within the limits and of an allowed type), copies it inside the bucket to `avatars/{users|groups}/{ownerId}/{newGuid}`, and saves the key and its outbox rows in one transaction behind a lock on the owner. The old object is removed after the commit, and by the relay if that failed, and never if it has become somebody else's picture by then.

`DELETE` on the same routes removes the picture (`{ "avatarUrl": null }`). Only an owner or admin can change a group's picture; the group's people are told with `ConversationUpdated`.

The browser crops the middle square and shrinks the picture to at most 256 pixels (WebP, else JPEG) before uploading, so a large photo becomes a few kilobytes.

## Serving pictures

The API serves pictures from the private bucket at a stable address that needs no token, because an `<img>` cannot send one:

`GET /api/avatars/users/{ownerId}/{fileId}` and `GET /api/avatars/groups/{ownerId}/{fileId}`

- Response: the image with `Cache-Control: public, max-age=31536000, immutable`.
- The address holds random ids that cannot be guessed or listed. A changed picture gets a new address, which is why a year's cache is safe, and the old address answers 404.
- Every request reads the object store, so the routes have their own limit: 600 per minute per client address, with misses counted. A missing picture is a plain 404 that is not logged as a warning.
- The stored `avatarUrl` is a relative address (`/api/avatars/...`).

## Limits

- The picture routes are open without a token on purpose; anyone who learns an address can load that picture.
- A request line in the proxy's logs holds the picture address.
- Deactivating one's own account is not offered.
