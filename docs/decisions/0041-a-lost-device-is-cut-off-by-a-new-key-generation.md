# A lost device is cut off by a new key generation, and a reset starts over

Status: Accepted

## Context

Removing a device ends its sessions, but the device still holds the private keys that open what is sealed to the person. For a device that was lost or stolen that is not enough: the keys must stop opening what is sent from then on. A person who has lost every device and the passphrase also needs a way back that does not leave their conversations unusable for ever. In both cases the conversations must move to new keys at once, without a window where a key sealed to the old keys is stored.

## Decision

**A lost device is removed together with a new generation of the account key.** `POST /api/keys/me/generations` stores the bundle of the next generation, replaces the backup that holds it (from the version the browser read), removes the named device with its sessions and marks every conversation the person is in as due for a new key period, in one transaction. The identity key stays, so the people who know the person see nothing change. The browser asks for the recovery passphrase first, which proves it is the person and keeps the new generation in the backup under the same passphrase.

**A reset is a separate operation.** `POST /api/keys/me/reset` stores a bundle with a new identity key and the next generation, replaces the backup whatever version it is at, removes every device and marks every conversation, after asking for the account password again under the rules of signing in. The old history cannot be opened by anyone, and the people who know the person must accept a new identity key.

**The generation is stored under a lock on the person's keys.** Every path that seals a conversation key to someone (a new key period, adding people, a new group, a new direct chat) reads that person's generation under the shared side of the lock, in the transaction that saves the epoch. The new generation is stored under the exclusive side. An epoch made for the old generation is therefore either saved before the new generation and then marked as due, or reads the new one and is refused, and never lands between the two with the mark already cleared.

**The other devices catch up with the passphrase.** The private half of the new generation is only in the backup, so a device that holds older keys shows a screen asking for the recovery passphrase, brings the newer keys and keeps them as the device it already is.

**Keys are also replaced when a week old.** The browsers replace a conversation's newest epoch when it is seven days old, whoever opens or sends first. The server reports when the epoch was made and still only holds back a replacement made within a day.

## Alternatives considered

- Removing the device and renewing the keys as two calls: a crash between them leaves a lost device removed but still able to read what is sealed next, or the keys renewed with the device still signed in.
- Row locks on the conversations of the person instead of a lock on their keys: an epoch that is being created, or a conversation that is being made, is not among the rows that exist when the lock is taken.
- Sealing the new private key to each device or to the old account key: a device key type was dropped from the design, and the old account key is what the lost device holds.
- Changing the identity key when a device is lost: every contact would be asked to accept a new key and every verified number would be lost, for what is usually a lost phone.
- Enforcing the week on the server, by refusing a message under an older epoch: a conversation where nobody opens the app for a week would refuse its first message, and a hostile member could not be made to replace keys anyway.

## Consequences

- A lost device keeps what it already read and the identity key. It cannot open what is sealed after the new generation, but it could still sign as the person if it also had a session. Starting over is what changes the identity.
- Other devices of the person are asked for the recovery passphrase once per new generation.
- A reset loses the person's old history and asks everyone who knows them to accept a new key. Whoever has the password and an unattended signed-in browser can do it.
- Every conversation the person is in is announced and replaced by the next member to open it, up to one epoch of up to 101 envelopes each.
- A client that never replaces keys after a week is not stopped.

## Related

- [Encryption keys and devices](../features/encryption-keys.md)
- [Safety numbers](../features/safety-numbers.md)
- [Conversation keys](../features/conversation-keys.md)
- [Conversation keys are made by members and checked by the server only for completeness](0037-conversation-keys-made-by-members.md)
- [Security model](../architecture/security-model.md)
