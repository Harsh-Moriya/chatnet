# Mentions are read by the server and saved with their cause

Status: Superseded

## Context

A client that names recipients could name someone outside the conversation, and a mention of a message that was never saved must not exist.

## Decision

A mention is `@userName` in the message text: 3 to 50 letters, digits or underscores with no word character on either side (so `ana@example.com` names nobody), compared without case, at most 20 per message. The people notified are the active members other than the sender whose account is not deactivated. Notification rows are added to the message's own `SaveChanges` (and an invitation to the group's), so a failed save or a lost idempotency race takes the notifications with it. Muting quiets new messages but a mention still notifies.

## Alternatives considered

- A list of mentioned ids sent by the client: can be forged.
- Matching display names: not unique and can contain spaces.
- A second save after the message, or an outbox for notifications: more moving parts than one transaction.

## Consequences

- A message cannot reveal who exists elsewhere or notify a whole conversation at once.
- A repeated send answers before it reaches the save, so it cannot notify twice.
- Superseded: the server cannot read the text of a message, so the sender's browser finds the names and sends the people as ids. The server keeps only active members other than the sender, which is the same protection the list of ids was passed over for here, and saves the notifications in the message's own transaction as before.

## Related

- [Notifications and mentions](../features/notifications.md)
- [Messages are sealed in the browser, and the server stores a blob it checks only for shape and epoch](0038-messages-stored-as-ciphertext.md)
