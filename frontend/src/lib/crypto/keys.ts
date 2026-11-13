// The keys of a person. The identity key (Ed25519) signs; it never changes unless the person
// resets their encryption, so other people can pin it. The account key (X25519) receives
// conversation keys; it is replaced (a new generation) when a device is removed, and the older
// ones are kept so history stays readable. The private halves live on the devices of the person
// and, encrypted, in the backup; the server only ever sees the signed public bundle.

import { CryptoError, concat, readU16, readU32, u16, u32, uuidBytes, type Bytes } from './bytes'
import { KEY_BYTES, LABELS, SIGNATURE_BYTES, SUITE_V1, VERSION, labelled } from './formats'
import { ed25519Sign, ed25519Verify, newEd25519, newX25519, type KeyPair } from './primitives'

export interface AccountKey extends KeyPair {
  generation: number
}

export interface AccountSecrets {
  identity: KeyPair
  // Ascending by generation.
  accounts: AccountKey[]
}

export async function createAccountSecrets(): Promise<AccountSecrets> {
  return { identity: await newEd25519(), accounts: [{ generation: 1, ...(await newX25519()) }] }
}

export async function rotateAccountKey(secrets: AccountSecrets): Promise<AccountSecrets> {
  const next = currentAccountKey(secrets).generation + 1
  return { identity: secrets.identity, accounts: [...secrets.accounts, { generation: next, ...(await newX25519()) }] }
}

// The keys of a person who starts over because the old ones can no longer be opened: a new identity key and one account key, as the
// generation after the newest the server holds. Nothing of the old keys is in it, so nothing sealed to them can be opened with it.
export async function startOverAccountSecrets(generation: number): Promise<AccountSecrets> {
  return { identity: await newEd25519(), accounts: [{ generation, ...(await newX25519()) }] }
}

export function currentAccountKey(secrets: AccountSecrets): AccountKey {
  return secrets.accounts[secrets.accounts.length - 1]
}

export function findAccountKey(secrets: AccountSecrets, generation: number): AccountKey | undefined {
  return secrets.accounts.find((account) => account.generation === generation)
}

// ##### Secrets encoding #####
// The plaintext inside the backup and inside a device link:
//   version(1) | identity private(32) | identity public(32) | count(2)
//   then per account key: generation(4) | private(32) | public(32)

const ACCOUNT_ENTRY_BYTES = 4 + 2 * KEY_BYTES

export function encodeSecrets(secrets: AccountSecrets): Bytes {
  if (secrets.accounts.length === 0) throw new CryptoError('malformed', 'At least one account key is needed')
  return concat(
    Uint8Array.of(VERSION),
    secrets.identity.privateKey,
    secrets.identity.publicKey,
    u16(secrets.accounts.length),
    ...secrets.accounts.map((account) => concat(u32(account.generation), account.privateKey, account.publicKey)),
  )
}

export function decodeSecrets(bytes: Uint8Array): AccountSecrets {
  const fixed = 1 + 2 * KEY_BYTES + 2
  if (bytes.length < fixed) throw new CryptoError('malformed', 'Secrets are too short')
  if (bytes[0] !== VERSION) throw new CryptoError('unsupported', 'Unknown secrets version')

  const count = readU16(bytes, fixed - 2)
  if (count === 0 || bytes.length !== fixed + count * ACCOUNT_ENTRY_BYTES) throw new CryptoError('malformed', 'Secrets have the wrong length')

  const accounts: AccountKey[] = []
  for (let i = 0; i < count; i++) {
    const at = fixed + i * ACCOUNT_ENTRY_BYTES
    const generation = readU32(bytes, at)
    if (i > 0 && generation <= accounts[i - 1].generation) throw new CryptoError('malformed', 'Generations must ascend')
    accounts.push({
      generation,
      privateKey: new Uint8Array(bytes.subarray(at + 4, at + 4 + KEY_BYTES)),
      publicKey: new Uint8Array(bytes.subarray(at + 4 + KEY_BYTES, at + ACCOUNT_ENTRY_BYTES)),
    })
  }
  return {
    identity: { privateKey: new Uint8Array(bytes.subarray(1, 1 + KEY_BYTES)), publicKey: new Uint8Array(bytes.subarray(1 + KEY_BYTES, 1 + 2 * KEY_BYTES)) },
    accounts,
  }
}

// ##### Public bundle #####
//   version(1) | suite(1) | generation(4) | identity public(32) | account public(32) | signature(64)
// The signature is the identity key over the label, the user id and the fields before it, so a
// bundle cannot be presented as belonging to another person.

export const BUNDLE_BYTES = 2 + 4 + 2 * KEY_BYTES + SIGNATURE_BYTES

export interface AccountBundle {
  userId: string
  generation: number
  identityPublic: Bytes
  accountPublic: Bytes
}

export function bundleSignatureInput(bundle: AccountBundle): Bytes {
  return labelled(LABELS.bundle, uuidBytes(bundle.userId), u32(bundle.generation), bundle.identityPublic, bundle.accountPublic)
}

export async function signBundle(userId: string, identity: KeyPair, account: AccountKey): Promise<Bytes> {
  const bundle: AccountBundle = { userId, generation: account.generation, identityPublic: identity.publicKey, accountPublic: account.publicKey }
  const signature = await ed25519Sign(identity.privateKey, bundleSignatureInput(bundle))
  return concat(Uint8Array.of(VERSION, SUITE_V1), u32(account.generation), identity.publicKey, account.publicKey, signature)
}

// Checks the bundle is internally consistent and signed by the identity key it carries. Whether
// that identity key is the one to trust is for the caller: it is pinned the first time it is seen.
export async function openBundle(userId: string, wire: Uint8Array): Promise<AccountBundle> {
  if (wire.length !== BUNDLE_BYTES) throw new CryptoError('malformed', 'Bundle has the wrong length')
  if (wire[0] !== VERSION || wire[1] !== SUITE_V1) throw new CryptoError('unsupported', 'Unknown bundle version')

  const bundle: AccountBundle = {
    userId,
    generation: readU32(wire, 2),
    identityPublic: new Uint8Array(wire.subarray(6, 6 + KEY_BYTES)),
    accountPublic: new Uint8Array(wire.subarray(6 + KEY_BYTES, 6 + 2 * KEY_BYTES)),
  }
  if (!(await ed25519Verify(bundle.identityPublic, bundleSignatureInput(bundle), wire.subarray(6 + 2 * KEY_BYTES)))) {
    throw new CryptoError('authentication', 'Bundle signature is not valid')
  }
  return bundle
}
