// Constants of version 1 of the formats. The byte layouts themselves are written down in
// docs/architecture/encryption-format.md; this file holds the numbers and labels they share.

import { utf8, concat, type Bytes } from './bytes'

// The first byte of every stored blob, and the second: the algorithm suite. Suite 1 is X25519,
// Ed25519, HKDF-SHA256, AES-256-GCM and PBKDF2-HMAC-SHA256.
export const VERSION = 1
export const SUITE_V1 = 1

export const KEY_BYTES = 32
export const NONCE_BYTES = 12
export const TAG_BYTES = 16
export const SIGNATURE_BYTES = 64
export const COMMITMENT_BYTES = 32

// Every HKDF info, AEAD associated-data and signature input starts with one of these labels and a
// zero byte, so a value made for one purpose can never be accepted for another.
export const LABELS = {
  bundle: 'chatnet/v1/account-bundle',
  backup: 'chatnet/v1/backup',
  envelope: 'chatnet/v1/envelope',
  commitment: 'chatnet/v1/epoch-commitment',
  epochSignature: 'chatnet/v1/epoch-signature',
  message: 'chatnet/v1/message',
  messageSignature: 'chatnet/v1/message-signature',
  fileChunk: 'chatnet/v1/file-chunk',
  link: 'chatnet/v1/link',
  linkCode: 'chatnet/v1/link-code',
  safety: 'chatnet/v1/safety',
} as const

export function labelled(label: string, ...parts: Uint8Array[]): Bytes {
  return concat(utf8(label), new Uint8Array([0]), ...parts)
}
