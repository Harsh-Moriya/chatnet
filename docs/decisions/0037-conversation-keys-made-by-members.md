# Conversation keys are made by members and checked by the server only for completeness

Status: Accepted

## Context

A conversation key must reach exactly the people who are meant to read, and the server must never hold a key that opens one. Yet the server is the only party that knows who is in a conversation at a given moment, and people join, leave and act at the same time from several devices.

## Decision

The browser of any member makes the key for the next epoch, seals it once for the current key of each person in the conversation, signs a record of who it was made for and sends all of it. The server stores it while holding the same per-conversation advisory lock as every change of membership, and refuses it unless it is the next number and has an envelope for exactly the people in the conversation, each at their newest key generation. It cannot open an envelope or check the signature; the members check those themselves. A conversation is created, and people are added, together with their epoch in one transaction. A removal or a leave only marks the epoch in force as due for replacement, and the next member to open the conversation makes the new one.

## Alternatives considered

- The server makes and seals the key: it would hold keys, which is what the design exists to prevent.
- The person who removes someone, or leaves, makes the next key in the same request: those requests have no body today, the person leaving would sign a record they are not in, and the request would race with every other change of membership.
- Letting the server accept any epoch and leaving completeness to the members: a member left out of an epoch could not read, and nothing would tell them until a message failed to open.
- Letting any member replace the key whenever they like: a member could store epochs without end, each with an envelope for every member that every other browser has to open, and the request limit would be the only brake.

## Consequences

- A member cannot replace the key more than once a day unless a removal or a leave marked it as due, so no one can fill the table with epochs; the price is that an epoch that does not check out stays for up to a day, and removing the member who made it marks it as due.
- Between a removal and the next epoch, the key in force is still held by the person who left; the conversation reports that it is due for replacement.
- A person who has not set up encryption keeps a conversation from getting new keys, and cannot be put in a new one.
- The server must hold, for each epoch, one envelope per member for as long as the epoch exists.
- A public key is trusted the first time a person is seen; after that a different one is refused until accepted.

## Related

- [Conversation keys](../features/conversation-keys.md)
- [Encryption formats](../architecture/encryption-format.md)
- [Group changes behind an advisory lock](0021-group-changes-behind-an-advisory-lock.md)
- [A transactional outbox with a SKIP LOCKED relay](0014-transactional-outbox.md)
