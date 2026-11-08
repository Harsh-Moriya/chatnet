// Linking a new device. The new device makes a temporary X25519 key and shows a link code; a
// device that is already signed in asks the server for the request that has this code, checks the
// code against the temporary public key it was given, and sends the secrets of the person sealed
// to that key. The server only relays the sealed box.
//
// The code is 50 bits of a hash of the temporary public key, written as 10 characters. Because it
// is derived from the key, a server that swaps in a key of its own would also have to find one
// whose hash starts with the same 50 bits, which cannot be done inside the minutes a link lives.
// A QR code carries the full request instead (see linkUri), with no server lookup.

import { CryptoError, concat, equal, fromBase64Url, toBase64Url, uuidBytes, type Bytes } from './bytes'
import { LABELS, labelled } from './formats'
import { decodeSecrets, encodeSecrets, type AccountSecrets } from './keys'
import { sha256, type KeyPair } from './primitives'
import { openSealed, sealToPublicKey, type SealOverrides } from './sealedBox'

// Crockford base32: no I, L, O or U, so a code read aloud or typed from a screen is hard to get wrong.
const ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
export const LINK_CODE_LENGTH = 10

export async function linkCode(temporaryPublic: Uint8Array): Promise<string> {
  const digest = await sha256(labelled(LABELS.linkCode, temporaryPublic))
  let code = ''
  for (let i = 0; i < LINK_CODE_LENGTH; i++) {
    const bit = i * 5
    const window = (digest[bit >> 3] << 8) | digest[(bit >> 3) + 1]
    code += ALPHABET[(window >> (16 - (bit & 7) - 5)) & 31]
  }
  return code
}

// Accepts what a person types: any case, spaces and hyphens, and the look-alike letters Crockford
// maps (O to 0, I and L to 1). Returns undefined when it cannot be a code.
export function normalizeLinkCode(input: string): string | undefined {
  const cleaned = input.toUpperCase().replace(/[\s-]/g, '').replace(/O/g, '0').replace(/[IL]/g, '1')
  return cleaned.length === LINK_CODE_LENGTH && [...cleaned].every((character) => ALPHABET.includes(character)) ? cleaned : undefined
}

export function formatLinkCode(code: string): string {
  return `${code.slice(0, 5)}-${code.slice(5)}`
}

export async function matchesLinkCode(code: string, temporaryPublic: Uint8Array): Promise<boolean> {
  const normalized = normalizeLinkCode(code)
  return normalized !== undefined && equal(new TextEncoder().encode(normalized), new TextEncoder().encode(await linkCode(temporaryPublic)))
}

// The content of the QR code: chatnet-link:1:<request id>:<base64url temporary public key>
const URI = /^chatnet-link:1:([0-9a-f-]{36}):([A-Za-z0-9_-]{43})$/i

export function linkUri(requestId: string, temporaryPublic: Uint8Array): string {
  return `chatnet-link:1:${requestId}:${toBase64Url(temporaryPublic)}`
}

export function parseLinkUri(uri: string): { requestId: string; temporaryPublic: Bytes } {
  const match = URI.exec(uri)
  if (!match) throw new CryptoError('malformed', 'Not a link address')
  uuidBytes(match[1])
  // 43 base64url characters are exactly the 32 bytes of a public key.
  return { requestId: match[1], temporaryPublic: fromBase64Url(match[2]) }
}

function linkContext(requestId: string, userId: string): Bytes {
  return concat(uuidBytes(requestId), uuidBytes(userId))
}

export function sealLinkSecrets(
  requestId: string,
  userId: string,
  temporaryPublic: Uint8Array,
  secrets: AccountSecrets,
  overrides?: SealOverrides,
): Promise<Bytes> {
  return sealToPublicKey(LABELS.link, linkContext(requestId, userId), temporaryPublic, encodeSecrets(secrets), overrides)
}

export async function openLinkSecrets(requestId: string, userId: string, temporary: KeyPair, sealed: Uint8Array): Promise<AccountSecrets> {
  return decodeSecrets(await openSealed(LABELS.link, linkContext(requestId, userId), temporary, sealed))
}
