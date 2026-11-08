// The encrypted backup of the secrets of a person, stored on the server. Only a key stretched from
// the recovery passphrase opens it; the server holds the blob and never the passphrase.
//
//   version(1) | suite(1) | kdf(1) | iterations(4) | salt(16) | nonce(12) | ciphertext | tag(16)
//
// kdf 1 is PBKDF2-HMAC-SHA256. The passphrase is Unicode-normalised to NFKC and encoded as UTF-8
// before it is stretched. The whole header is authenticated, together with the user id.

import { CryptoError, concat, readU32, u32, utf8, uuidBytes, type Bytes } from './bytes'
import { KEY_BYTES, LABELS, NONCE_BYTES, SUITE_V1, VERSION, labelled } from './formats'
import { aesGcmDecrypt, aesGcmEncrypt, pbkdf2, randomBytes } from './primitives'
import { decodeSecrets, encodeSecrets, type AccountSecrets } from './keys'

export const KDF_PBKDF2_SHA256 = 1
export const DEFAULT_ITERATIONS = 600_000
export const MAX_ITERATIONS = 10_000_000
export const PASSPHRASE_MIN_LENGTH = 12

const SALT_BYTES = 16
const HEADER_BYTES = 3 + 4 + SALT_BYTES + NONCE_BYTES

// Fixed inputs for the published test vectors, and a lower cost for tests. Real use leaves them out.
export interface BackupOverrides {
  salt?: Uint8Array
  nonce?: Uint8Array
  iterations?: number
}

// Length is counted in characters (code points) after normalisation, so it means the same on
// every platform.
export function passphraseLength(passphrase: string): number {
  return [...passphrase.normalize('NFKC')].length
}

async function backupKey(passphrase: string, salt: Uint8Array, iterations: number): Promise<Bytes> {
  return pbkdf2(utf8(passphrase.normalize('NFKC')), salt, iterations, KEY_BYTES)
}

export async function sealBackup(userId: string, passphrase: string, secrets: AccountSecrets, overrides: BackupOverrides = {}): Promise<Bytes> {
  if (passphraseLength(passphrase) < PASSPHRASE_MIN_LENGTH) throw new CryptoError('malformed', 'Passphrase is too short')
  const iterations = overrides.iterations ?? DEFAULT_ITERATIONS
  // A fractional count is refused further down, when the count is written into the header.
  if (iterations < 1 || iterations > MAX_ITERATIONS) throw new CryptoError('malformed', 'Iterations are out of range')

  const salt = overrides.salt ?? randomBytes(SALT_BYTES)
  const nonce = overrides.nonce ?? randomBytes(NONCE_BYTES)
  const header = concat(Uint8Array.of(VERSION, SUITE_V1, KDF_PBKDF2_SHA256), u32(iterations), salt, nonce)
  const key = await backupKey(passphrase, salt, iterations)
  const sealed = await aesGcmEncrypt(key, nonce, encodeSecrets(secrets), labelled(LABELS.backup, uuidBytes(userId), header))
  return concat(header, sealed)
}

// A wrong passphrase and a damaged blob are indistinguishable: both fail authentication.
export async function openBackup(userId: string, passphrase: string, blob: Uint8Array): Promise<AccountSecrets> {
  if (blob.length <= HEADER_BYTES) throw new CryptoError('malformed', 'Backup is too short')
  if (blob[0] !== VERSION || blob[1] !== SUITE_V1 || blob[2] !== KDF_PBKDF2_SHA256) throw new CryptoError('unsupported', 'Unknown backup version')

  const iterations = readU32(blob, 3)
  if (iterations < 1 || iterations > MAX_ITERATIONS) throw new CryptoError('malformed', 'Iterations are out of range')

  const header = blob.subarray(0, HEADER_BYTES)
  const salt = blob.subarray(7, 7 + SALT_BYTES)
  const nonce = blob.subarray(7 + SALT_BYTES, HEADER_BYTES)
  const key = await backupKey(passphrase, salt, iterations)
  const plain = await aesGcmDecrypt(key, nonce, blob.subarray(HEADER_BYTES), labelled(LABELS.backup, uuidBytes(userId), header))
  return decodeSecrets(plain)
}
