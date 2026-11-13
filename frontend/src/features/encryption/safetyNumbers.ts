import { formatSafetyNumber, fromBase64Url, openBundle, safetyNumber } from '../../lib/crypto'
import { MissingKeysError } from './epochs'
import { IdentityChangedError, pinIdentity, trustNewIdentity } from './pins'
import type { EpochApi, Pin, PinStore } from './types'

// What a person sees to be sure they are talking to who they think: a number made from both identity keys that the two of them can read
// out or compare on screen. Nothing here touches the network, IndexedDB or the page directly, so a test drives the real cryptography
// against fakes.

export interface SafetyDeps {
  api: Pick<EpochApi, 'lookupKeys'>
  pins: PinStore
  self: { userId: string; identityPublic: Uint8Array }
}

export interface SafetyView {
  // Twelve groups of five digits.
  number: string
  // When this person was marked as verified with the key shown, or null.
  verifiedAt: string | null
  // The key this person has now, when it is not the one this browser trusts: the number shown is for that key, and it is for the person
  // looking to compare it before accepting.
  changedTo: Uint8Array | null
}

// The number for this person, from the identity key the server gives out for them now. The first time a person is seen the key is
// remembered, as it is for anything else that seals a key to them; a key that is not the remembered one is reported and not remembered.
export async function loadSafetyView(deps: SafetyDeps, contactId: string): Promise<SafetyView> {
  const found = (await deps.api.lookupKeys([contactId])).find((entry) => entry.userId === contactId)
  if (!found) throw new MissingKeysError([contactId])

  const bundle = await openBundle(contactId, fromBase64Url(found.bundle))

  let changedTo: Uint8Array | null = null
  try {
    await pinIdentity(deps.pins, deps.self.userId, contactId, bundle.identityPublic)
  } catch (failure) {
    if (!(failure instanceof IdentityChangedError)) throw failure
    changedTo = failure.seen
  }

  const pin = await deps.pins.load(deps.self.userId, contactId)
  const shown = changedTo ?? pin!.identityPublic
  const number = await safetyNumber(
    { userId: deps.self.userId, identityPublic: deps.self.identityPublic },
    { userId: contactId, identityPublic: shown },
  )

  return { number: formatSafetyNumber(number), verifiedAt: changedTo === null ? (pin?.verifiedAt ?? null) : null, changedTo }
}

// Marks the person as verified, or takes the mark away. Only a person whose key is remembered can be: the mark is about that key.
export async function setVerified(deps: Pick<SafetyDeps, 'pins' | 'self'>, contactId: string, verified: boolean): Promise<void> {
  const pin = await deps.pins.load(deps.self.userId, contactId)
  if (!pin) throw new Error('This person has not been seen yet, so there is no key to verify.')

  const unmarked: Pin = { ownerId: pin.ownerId, contactId: pin.contactId, identityPublic: pin.identityPublic, firstSeenAt: pin.firstSeenAt }
  await deps.pins.save(verified ? { ...unmarked, verifiedAt: new Date().toISOString() } : unmarked)
}

// The person looking accepted the key they were shown. It replaces the remembered one, and the verified mark does not carry over to it.
export async function acceptChangedIdentity(
  deps: Pick<SafetyDeps, 'pins' | 'self'>,
  contactId: string,
  seen: Uint8Array,
): Promise<void> {
  await trustNewIdentity(deps.pins, deps.self.userId, contactId, seen)
}
