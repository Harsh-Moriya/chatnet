# Web Push

Alerts for a person who has the app closed. Optional: the server offers it only when it is given keys.

## Behaviour

- A switch in the profile turns alerts on for **one browser**. The browser asks permission, subscribes to its vendor's push service, and registers the subscription with the API.
- A person with **no live connection** gets an alert for a new message in a conversation that is not muted, a mention, or an invitation to a group. The text is the text of the in-page alert.
- "No live connection" is asked of Redis when the relay takes the row (the unexpired connection entries of the user), not the "online" of presence, which includes a short wait after the last connection closes. If Redis cannot say who is connected, the alert waits.
- Alerts are outbox rows for the relay alone (`MessageAlertRequested`, `NotificationAlertRequested`), saved in the transaction of the change, so an alert survives a crash. Delivery is at least once; an alert with the same tag replaces the one before it on the screen.
- A person who is connected gets the in-page alert instead.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/push/key` | the server's public key, which the browser subscribes with; 404 when push is off |
| `PUT /api/push/subscriptions` | register or renew a browser `{ endpoint, keys: { p256dh, auth } }` (the shape of `PushSubscription.toJSON()`); 204 |
| `DELETE /api/push/subscriptions` | remove a browser `{ endpoint }`; 204 whether or not it was registered, and works when push is off so a browser can always leave |

## Encryption and signing

The payload is encrypted to the one browser it is for (RFC 8291, the `aes128gcm` content encoding), and the request is signed with VAPID (RFC 8292). Both are implemented on `System.Security.Cryptography` in this repository, with no package. The encryption is checked against the worked example in RFC 8291 byte for byte, and was accepted by Google's push service.

The push service sees which endpoint, when and how big, never the text.

## Rules for registrations

- One row per browser, an upsert on the endpoint. A browser registered to someone else moves to the caller. At most 10 browsers per person; the least recently used is dropped.
- The endpoint is a capability and a request the server makes for a client, so only `https` addresses of the push services in `Push:AllowedHosts` are accepted, checked at registration and before every push, with no redirects followed. See [security model](../architecture/security-model.md).
- A 404 or 410 from the push service removes the registration. Other refusals are logged and the registration kept, and the alert is tried again by the relay.
- Registrations unused for 60 days are removed by a daily job.

## In the browser

The page decides what to do from facts (permission, existing subscription, the server's key, the person's choice), keeps the choice per person in the browser, ends a subscription it finds for a person who did not ask for it, and releases the browser on sign-out. The service worker shows the alert, and a click focuses or opens the app at the conversation.

## Configuration

`Push:PublicKey`, `Push:PrivateKey` and `Push:Subject` identify the server. With none set, push is off: the feature is not offered and no registration is kept. With only some set, or a pair that does not match, the host does not start. The private key is a secret: user-secrets or an environment variable, never a committed file. See [configuration](../operations/configuration.md) for the others, and [running locally](../operations/running-locally.md) for a one-line key generator.

The API container must be able to reach the push services on port 443 (`fcm.googleapis.com`, `updates.push.services.mozilla.com`, `push.apple.com`, `notify.windows.com`).

## Limits

- Tried against Chrome and Google's push service. Firefox, Safari and Edge use the same standard but have not been tried.
- An iPhone needs the app installed to the home screen, which needs a web app manifest and icons that the app does not have yet.
- Changing the key pair makes every browser subscribe again, which they do by themselves the next time the page is open.
- 20 registration calls per minute per person.
