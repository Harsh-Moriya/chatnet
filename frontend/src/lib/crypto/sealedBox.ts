// Encrypts a small value to an X25519 public key (an ECIES-style sealed box). Used for the
// conversation key envelopes and for the secrets handed to a device being linked; only the label
// and the context bytes differ between them.
//
//   version(1) | suite(1) | ephemeral public key(32) | nonce(12) | ciphertext | tag(16)
//
// A fresh ephemeral key makes every box unique, and the label and context are mixed into both the
// derived key and the associated data, so a box made for one purpose, conversation, epoch or
// person fails to open anywhere else.

import { CryptoError, concat, type Bytes } from './bytes'
import { KEY_BYTES, NONCE_BYTES, SUITE_V1, TAG_BYTES, VERSION, labelled } from './formats'
import { aesGcmDecrypt, aesGcmEncrypt, hkdf, newX25519, randomBytes, x25519Derive, x25519FromPrivate, type KeyPair } from './primitives'

const HEADER_BYTES = 2 + KEY_BYTES
export const SEALED_OVERHEAD = HEADER_BYTES + NONCE_BYTES + TAG_BYTES

// Fixed inputs for the published test vectors. Real use leaves them out.
export interface SealOverrides {
  ephemeralPrivate?: Uint8Array
  nonce?: Uint8Array
}

async function boxKey(label: string, context: Uint8Array, secret: Uint8Array, ephemeralPublic: Uint8Array, recipientPublic: Uint8Array) {
  const info = labelled(label, context)
  const key = await hkdf(secret, concat(ephemeralPublic, recipientPublic), info, KEY_BYTES)
  return { key, info }
}

export async function sealToPublicKey(
  label: string,
  context: Uint8Array,
  recipientPublic: Uint8Array,
  plaintext: Uint8Array,
  overrides: SealOverrides = {},
): Promise<Bytes> {
  const ephemeral = overrides.ephemeralPrivate ? await x25519FromPrivate(overrides.ephemeralPrivate) : await newX25519()
  const secret = await x25519Derive(ephemeral.privateKey, recipientPublic)
  const { key, info } = await boxKey(label, context, secret, ephemeral.publicKey, recipientPublic)
  const header = concat(Uint8Array.of(VERSION, SUITE_V1), ephemeral.publicKey)
  const nonce = overrides.nonce ?? randomBytes(NONCE_BYTES)
  const sealed = await aesGcmEncrypt(key, nonce, plaintext, concat(info, header))
  return concat(header, nonce, sealed)
}

export async function openSealed(label: string, context: Uint8Array, recipient: KeyPair, box: Uint8Array): Promise<Bytes> {
  if (box.length < SEALED_OVERHEAD) throw new CryptoError('malformed', 'Sealed box is too short')
  if (box[0] !== VERSION || box[1] !== SUITE_V1) throw new CryptoError('unsupported', 'Unknown sealed box version')

  const header = box.subarray(0, HEADER_BYTES)
  const ephemeralPublic = box.subarray(2, HEADER_BYTES)
  const nonce = box.subarray(HEADER_BYTES, HEADER_BYTES + NONCE_BYTES)
  const sealed = box.subarray(HEADER_BYTES + NONCE_BYTES)

  const secret = await x25519Derive(recipient.privateKey, ephemeralPublic)
  const { key, info } = await boxKey(label, context, secret, ephemeralPublic, recipient.publicKey)
  return aesGcmDecrypt(key, nonce, sealed, concat(info, header))
}
