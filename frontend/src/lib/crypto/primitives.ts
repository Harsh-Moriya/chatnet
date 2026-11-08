// The primitives of the formats, on WebCrypto and nothing else. Keys cross this boundary as raw
// 32-byte strings (the same bytes every platform has); WebCrypto cannot import a raw private
// key, so the private half is wrapped in the fixed PKCS8 header of its algorithm for the call.

import { CryptoError, bytesOf, concat, fromBase64Url, type Bytes } from './bytes'
import { KEY_BYTES, NONCE_BYTES, SIGNATURE_BYTES, TAG_BYTES } from './formats'

const subtle = crypto.subtle

const PKCS8_X25519 = Uint8Array.of(0x30, 0x2e, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x6e, 0x04, 0x22, 0x04, 0x20)
const PKCS8_ED25519 = Uint8Array.of(0x30, 0x2e, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x04, 0x22, 0x04, 0x20)

export interface KeyPair {
  // The 32-byte private key (an X25519 scalar, or an Ed25519 seed).
  privateKey: Bytes
  publicKey: Bytes
}

export function randomBytes(length: number): Bytes {
  return crypto.getRandomValues(new Uint8Array(length))
}

export async function sha256(data: Uint8Array): Promise<Bytes> {
  return new Uint8Array(await subtle.digest('SHA-256', bytesOf(data)))
}

export async function hkdf(secret: Uint8Array, salt: Uint8Array, info: Uint8Array, length: number): Promise<Bytes> {
  const key = await subtle.importKey('raw', bytesOf(secret), 'HKDF', false, ['deriveBits'])
  const bits = await subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: bytesOf(salt), info: bytesOf(info) }, key, length * 8)
  return new Uint8Array(bits)
}

export async function pbkdf2(password: Uint8Array, salt: Uint8Array, iterations: number, length: number): Promise<Bytes> {
  const key = await subtle.importKey('raw', bytesOf(password), 'PBKDF2', false, ['deriveBits'])
  const bits = await subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt: bytesOf(salt), iterations }, key, length * 8)
  return new Uint8Array(bits)
}

function requireLength(value: Uint8Array, length: number, what: string): void {
  if (value.length !== length) throw new CryptoError('malformed', `${what} must be ${length} bytes`)
}

// Output is the ciphertext followed by the 16-byte tag.
export async function aesGcmEncrypt(key: Uint8Array, nonce: Uint8Array, plaintext: Uint8Array, aad: Uint8Array): Promise<Bytes> {
  requireLength(key, KEY_BYTES, 'An AES key')
  requireLength(nonce, NONCE_BYTES, 'A nonce')
  const imported = await subtle.importKey('raw', bytesOf(key), 'AES-GCM', false, ['encrypt'])
  const sealed = await subtle.encrypt({ name: 'AES-GCM', iv: bytesOf(nonce), additionalData: bytesOf(aad), tagLength: TAG_BYTES * 8 }, imported, bytesOf(plaintext))
  return new Uint8Array(sealed)
}

export async function aesGcmDecrypt(key: Uint8Array, nonce: Uint8Array, sealed: Uint8Array, aad: Uint8Array): Promise<Bytes> {
  requireLength(key, KEY_BYTES, 'An AES key')
  requireLength(nonce, NONCE_BYTES, 'A nonce')
  if (sealed.length < TAG_BYTES) throw new CryptoError('malformed', 'Ciphertext is shorter than its tag')
  const imported = await subtle.importKey('raw', bytesOf(key), 'AES-GCM', false, ['decrypt'])
  try {
    const plain = await subtle.decrypt({ name: 'AES-GCM', iv: bytesOf(nonce), additionalData: bytesOf(aad), tagLength: TAG_BYTES * 8 }, imported, bytesOf(sealed))
    return new Uint8Array(plain)
  } catch {
    throw new CryptoError('authentication', 'Ciphertext did not authenticate')
  }
}

// The public half is read back from the JWK form of the imported key, the one place WebCrypto
// computes it.
async function publicHalf(algorithm: 'X25519' | 'Ed25519', privateKey: Uint8Array): Promise<Bytes> {
  requireLength(privateKey, KEY_BYTES, 'A private key')
  const header = algorithm === 'X25519' ? PKCS8_X25519 : PKCS8_ED25519
  const key = await subtle.importKey('pkcs8', concat(header, privateKey), algorithm, true, algorithm === 'X25519' ? ['deriveBits'] : ['sign'])
  const jwk = await subtle.exportKey('jwk', key)
  if (!jwk.x) throw new CryptoError('unsupported', 'This browser does not expose the public key')
  return fromBase64Url(jwk.x)
}

export async function x25519FromPrivate(privateKey: Uint8Array): Promise<KeyPair> {
  return { privateKey: bytesOf(privateKey), publicKey: await publicHalf('X25519', privateKey) }
}

export async function newX25519(): Promise<KeyPair> {
  return x25519FromPrivate(randomBytes(KEY_BYTES))
}

export async function ed25519FromSeed(seed: Uint8Array): Promise<KeyPair> {
  return { privateKey: bytesOf(seed), publicKey: await publicHalf('Ed25519', seed) }
}

export async function newEd25519(): Promise<KeyPair> {
  return ed25519FromSeed(randomBytes(KEY_BYTES))
}

// Diffie-Hellman. A point of small order gives an all-zero secret. Browsers differ on whether they
// throw or return the zeros, so both are refused with the same error.
export async function x25519Derive(privateKey: Uint8Array, peerPublicKey: Uint8Array): Promise<Bytes> {
  requireLength(privateKey, KEY_BYTES, 'A private key')
  requireLength(peerPublicKey, KEY_BYTES, 'A public key')
  const own = await subtle.importKey('pkcs8', concat(PKCS8_X25519, privateKey), 'X25519', false, ['deriveBits'])
  const peer = await subtle.importKey('raw', bytesOf(peerPublicKey), 'X25519', false, [])
  let secret: Bytes
  try {
    secret = new Uint8Array(await subtle.deriveBits({ name: 'X25519', public: peer }, own, 256))
  } catch {
    throw new CryptoError('malformed', 'Public key cannot be used')
  }
  if (secret.every((byte) => byte === 0)) throw new CryptoError('malformed', 'Public key has small order')
  return secret
}

export async function ed25519Sign(seed: Uint8Array, data: Uint8Array): Promise<Bytes> {
  requireLength(seed, KEY_BYTES, 'A private key')
  const key = await subtle.importKey('pkcs8', concat(PKCS8_ED25519, seed), 'Ed25519', false, ['sign'])
  return new Uint8Array(await subtle.sign('Ed25519', key, bytesOf(data)))
}

export async function ed25519Verify(publicKey: Uint8Array, data: Uint8Array, signature: Uint8Array): Promise<boolean> {
  requireLength(publicKey, KEY_BYTES, 'A public key')
  if (signature.length !== SIGNATURE_BYTES) return false
  const key = await subtle.importKey('raw', bytesOf(publicKey), 'Ed25519', false, ['verify'])
  return subtle.verify('Ed25519', key, bytesOf(signature), bytesOf(data))
}
