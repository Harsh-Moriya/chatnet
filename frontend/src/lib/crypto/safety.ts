// The safety number of two people: 60 digits both can read out or compare on screen. It is made
// from the identity public keys the server handed out, so if the server had given either person a
// key of its own, the numbers on the two screens would differ.
//
// Each person contributes 30 digits: SHA-256 of the label, the user id and the identity public
// key, read as six 40-bit numbers, each reduced modulo 100000 and written with five digits. The
// person with the lower user id (compared as 16 bytes) comes first, so both sides see the same
// number.

import { compare, uuidBytes } from './bytes'
import { LABELS, labelled } from './formats'
import { sha256 } from './primitives'

export interface IdentityKey {
  userId: string
  identityPublic: Uint8Array
}

async function digits(person: IdentityKey): Promise<string> {
  const digest = await sha256(labelled(LABELS.safety, uuidBytes(person.userId), person.identityPublic))
  let out = ''
  for (let group = 0; group < 6; group++) {
    let value = 0
    for (let i = 0; i < 5; i++) value = value * 256 + digest[group * 5 + i]
    out += String(value % 100000).padStart(5, '0')
  }
  return out
}

export async function safetyNumber(a: IdentityKey, b: IdentityKey): Promise<string> {
  const [first, second] = compare(uuidBytes(a.userId), uuidBytes(b.userId)) <= 0 ? [a, b] : [b, a]
  return (await digits(first)) + (await digits(second))
}

// Twelve groups of five digits, as shown to people.
export function formatSafetyNumber(number: string): string {
  return number.match(/.{5}/g)?.join(' ') ?? number
}
