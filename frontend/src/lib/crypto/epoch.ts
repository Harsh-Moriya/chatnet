// The key of a conversation for one epoch, and how it reaches the members. An epoch is a period
// during which one 32-byte AES key encrypts the messages; a new epoch starts when the membership
// changes, so a person who left cannot read what comes after.
//
// The key is delivered as one envelope per member (a sealed box to the account key of that member).
// The creator also signs a record of the epoch: its commitment to the key and the exact list of
// members who received it. Recipients check the signature and that their key matches the
// commitment, so a creator cannot give different members different keys, and the server cannot
// invent an epoch.

import { CryptoError, compare, concat, equal, u32, uuidBytes, type Bytes } from './bytes'
import { COMMITMENT_BYTES, KEY_BYTES, LABELS, labelled } from './formats'
import { ed25519Sign, ed25519Verify, hkdf, randomBytes, type KeyPair } from './primitives'
import { openSealed, sealToPublicKey, type SealOverrides } from './sealedBox'

export function newEpochKey(): Bytes {
  return randomBytes(KEY_BYTES)
}

// HKDF of the key bound to the conversation and the epoch. It reveals nothing about the key.
export async function epochCommitment(conversationId: string, epoch: number, epochKey: Uint8Array): Promise<Bytes> {
  return hkdf(epochKey, new Uint8Array(0), labelled(LABELS.commitment, uuidBytes(conversationId), u32(epoch)), COMMITMENT_BYTES)
}

// ##### Envelope #####
// A sealed box (94 bytes) holding the epoch key, bound to the conversation, epoch, member and the
// generation of the account key it was sealed to.

export interface EnvelopeTarget {
  conversationId: string
  epoch: number
  userId: string
  generation: number
}

function envelopeContext(target: EnvelopeTarget): Bytes {
  return concat(uuidBytes(target.conversationId), u32(target.epoch), uuidBytes(target.userId), u32(target.generation))
}

export function sealEnvelope(target: EnvelopeTarget, accountPublic: Uint8Array, epochKey: Uint8Array, overrides?: SealOverrides): Promise<Bytes> {
  if (epochKey.length !== KEY_BYTES) throw new CryptoError('malformed', 'An epoch key is 32 bytes')
  return sealToPublicKey(LABELS.envelope, envelopeContext(target), accountPublic, epochKey, overrides)
}

export async function openEnvelope(target: EnvelopeTarget, accountKey: KeyPair, envelope: Uint8Array): Promise<Bytes> {
  const key = await openSealed(LABELS.envelope, envelopeContext(target), accountKey, envelope)
  if (key.length !== KEY_BYTES) throw new CryptoError('malformed', 'An epoch key is 32 bytes')
  return key
}

// ##### Signed record #####
// The signature input: label, conversation, epoch, creator, commitment, the number of members and
// each member (user id and account key generation) in ascending order of user id.

export interface EpochMember {
  userId: string
  generation: number
}

export interface EpochRecord {
  conversationId: string
  epoch: number
  creatorId: string
  commitment: Uint8Array
  members: EpochMember[]
}

export function epochSignatureInput(record: EpochRecord): Bytes {
  const members = record.members.map((member) => ({ id: uuidBytes(member.userId), generation: member.generation })).sort((a, b) => compare(a.id, b.id))
  for (let i = 1; i < members.length; i++) {
    if (equal(members[i - 1].id, members[i].id)) throw new CryptoError('malformed', 'A member appears twice')
  }
  if (record.commitment.length !== COMMITMENT_BYTES) throw new CryptoError('malformed', 'A commitment is 32 bytes')

  return labelled(
    LABELS.epochSignature,
    uuidBytes(record.conversationId),
    u32(record.epoch),
    uuidBytes(record.creatorId),
    record.commitment,
    u32(members.length),
    ...members.map((member) => concat(member.id, u32(member.generation))),
  )
}

export function signEpoch(record: EpochRecord, creatorIdentity: KeyPair): Promise<Bytes> {
  return ed25519Sign(creatorIdentity.privateKey, epochSignatureInput(record))
}

export function verifyEpoch(record: EpochRecord, creatorIdentityPublic: Uint8Array, signature: Uint8Array): Promise<boolean> {
  return ed25519Verify(creatorIdentityPublic, epochSignatureInput(record), signature)
}
