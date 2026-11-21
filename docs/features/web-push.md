# Web Push

Alerts for a person who has the app closed. Optional: the server offers it only when it is given keys.

## Behaviour

- A switch in the profile turns alerts on for **one browser**. The browser asks permission, subscribes to its vendor's push service, and registers the subscription with the API.
- A person with **no live connection** gets an alert for a new message in a conversation that is not muted, a mention, or an invitation to a group. The text says who it is from and where, and never what was said: the server cannot read messages. A message alert has the sender's name as its title (with the group's name for a group) and "New message", or "Sent a file" or the number of files, as its body. A mention says that someone mentioned the person, with no body. An invitation says who added them to which group. The alert shown by an open page is richer, because the page can open the message.
- A **ringing call** alerts a callee with no live connection too, with the caller's name and "Incoming audio call" (or video), of type `call`, tagged `call-{call id}`, and it stays on the screen until it is used or dismissed. It is pushed at once when the call starts, and only while the call still rings, the callee is away and has not muted the conversation. A call that ends unanswered replaces it, under the same tag, with the "Missed audio call" alert (type `missed-call`), which also skips a member who muted the conversation.
- "No live connection" is asked of Redis when the relay takes the row (the unexpired connection entries of the user), not the "online" of presence, which includes a short wait after the last connection closes. If Redis cannot say who is connected, the alert waits.
- Alerts are outbox rows for the relay alone (`MessageAlertRequested`, `NotificationAlertRequested`, `CallAlertRequested`; the last is pushed at once and the relay only takes it if that failed), saved in the transaction of the change, so an alert survives a crash. Delivery is at least once; an alert with the same tag replaces the one before it on the screen.
- A person who is connected gets the in-page alert instead.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/push/key` | the server's public key, which the browser subscribes with; 404 when push is off |
| `PUT /api/push/subscriptions` | register or renew a browser `{ endpoint, keys: { p256dh, auth } }` (the shape of `PushSubscription.toJSON()`); 204 |
| `DELETE /api/push/subscriptions` | remove a browser `{ endpoint }`; 204 whether or not it was registered, and works when push is off so a browser can always leave |

## Encryption and signing

The payload is encrypted to the one browser it is for (RFC 8291, the `aes128gcm` content encoding), and the request is signed with VAPID (RFC 8292). Both are implemented on `System.Security.Cryptography` in this repository, with no package. The encryption is checked against the worked example in RFC 8291 byte for byte, and was accepted by Google's push service.

The push service sees which endpoint, when and how big. The payload names the sender and the conversation and carries no text of any message.

## Rules for registrations

- One row per browser, an upsert on the endpoint. A browser registered to someone else moves to the caller. At most 10 browsers per person; the least recently used is dropped.
- The endpoint is a capability and a request the server makes for a client, so only `https` addresses of the push services in `Push:AllowedHosts` are accepted, checked at registration and before every push, with no redirects followed. See [security model](../architecture/security-model.md).
- A 404 or 410 from the push service removes the registration. Other refusals are logged and the registration kept, and the alert is tried again by the relay.
- Registrations unused for 60 days are removed by a daily job.

## In the browser

The page decides what to do from facts (permission, existing subscription, the server's key, the person's choice), keeps the choice per person in the browser, ends a subscription it finds for a person who did not ask for it, and releases the browser on sign-out. The service worker shows the alert, and a click focuses or opens the app at the conversation. The same worker, which is registered when the application starts, also plays encrypted video (see [attachments](attachments.md#videos-in-the-viewer)); it is built from TypeScript into one classic script served at `/sw.js`.

## Configuration

`Push:PublicKey`, `Push:PrivateKey` and `Push:Subject` identify the server. With none set, push is off: the feature is not offered and no registration is kept. With only some set, or a pair that does not match, the host does not start. The private key is a secret: user-secrets or an environment variable, never a committed file. See [configuration](../operations/configuration.md) for the others, and [running locally](../operations/running-locally.md) for a one-line key generator.

The API container must be able to reach the push services on port 443 (`fcm.googleapis.com`, `updates.push.services.mozilla.com`, `push.apple.com`, `notify.windows.com`).

## On an Android phone

The app installed to the home screen from Chrome on Android gets alerts in the notification shade like any other app: pushed ones when the app is closed, and its own when it is open but not in front. A few things are particular to Android:

- **The permission is the phone's.** Turning the switch on in the profile asks for it; if it was refused, the phone's own settings (Settings, Apps, ChatNET, Notifications) are where to allow it again.
- **The page's own alerts go through the service worker.** Chrome for Android refuses `new Notification` (it throws), so the page raises an alert with `registration.showNotification`, the way the worker does for a push; both use one look (`frontend/src/lib/alertLook.ts`: the picture, the small white status bar badge, a vibration pattern, and a tag so a conversation shows one alert that sounds again for each new message). A click opens the conversation.
- **A phone that has just left the app is still "connected" for a while.** The server pushes only to a person with no live connection, and the page keeps its connection after the person leaves it, until the connection drops or its 90 second entry expires. In that time the page raises the alert itself; after it, the server pushes. Swiping the app away drops the connection at once.
- The manifest has a stable `id`, so Android treats the installed app as one app across updates.

### If nothing arrives on a phone

Check in this order:

1. The deployment has `PUSH_PUBLIC_KEY`, `PUSH_PRIVATE_KEY` and `PUSH_SUBJECT` in `.env.prod` (they are optional, and without them the feature is off), and the API containers were recreated after they were set (`docker compose --env-file .env.prod -f docker-compose.prod.yml up -d`). With them set, the profile shows the closed-app alerts switch; without them it is not offered.
2. The switch is on **inside the installed app**, and the phone allows notifications for it.
3. `docker compose --env-file .env.prod -f docker-compose.prod.yml logs api | grep -i push` shows the pushes that were sent, or the push service's refusals ("was refused", "was not taken").
4. The API container can reach `fcm.googleapis.com` on port 443.
5. A phone in battery saver or restricted background mode may delay a push; the app's entry in Android's battery settings should be unrestricted.

## Limits

- Tried against Chrome and Google's push service. Firefox, Safari and Edge use the same standard but have not been tried.
- An iPhone needs the app installed to the home screen (the manifest and icons are there now); this has not been tried on an iPhone.
- Tried against Chrome on a computer; **nothing has been tried on a real Android phone yet**. The page's side was checked in a desktop browser made to behave as Chrome for Android does (its constructor throws).
- Changing the key pair makes every browser subscribe again, which they do by themselves the next time the page is open.
- 20 registration calls per minute per person.
