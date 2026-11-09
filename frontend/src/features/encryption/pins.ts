import { equal } from '../../lib/crypto'
import type { PinStore } from './types'

// Remembering who is who. The first time a person's identity key is seen it is kept, and from then on a different one is refused
// until the person who is looking accepts it: the server hands out public keys, so a key that changes without anyone having
// reset their encryption is the server, or someone with access to it, putting itself between two people.

// A person's identity key is not the one it was when it was first seen here. `seen` is the key that was just offered.
export class IdentityChangedError extends Error {
  readonly userId: string
  readonly seen: Uint8Array

  constructor(userId: string, seen: Uint8Array) {
    super('The identity key of this person changed.')
    this.name = 'IdentityChangedError'
    this.userId = userId
    this.seen = seen
  }
}

// 'new' the first time a person is seen, 'same' after that. Throws when the key is not the one that was kept.
export async function pinIdentity(
  store: PinStore,
  ownerId: string,
  contactId: string,
  identityPublic: Uint8Array,
): Promise<'new' | 'same'> {
  const pin = await store.load(ownerId, contactId)

  if (pin === null) {
    await store.save({ ownerId, contactId, identityPublic, firstSeenAt: new Date().toISOString() })
    return 'new'
  }

  if (equal(pin.identityPublic, identityPublic)) return 'same'

  throw new IdentityChangedError(contactId, identityPublic)
}

// The person looking accepted the key they are now seen with, so it replaces the one that was kept.
export async function trustNewIdentity(store: PinStore, ownerId: string, contactId: string, identityPublic: Uint8Array): Promise<void> {
  await store.save({ ownerId, contactId, identityPublic, firstSeenAt: new Date().toISOString() })
}
