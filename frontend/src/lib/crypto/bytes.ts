// Byte helpers shared by the encryption code. Everything in the formats is a plain byte string:
// integers are big-endian, text is UTF-8, and ids are the 16 bytes of a UUID in the order its
// hex digits are written (not the mixed-endian layout .NET uses for a Guid in memory).

// WebCrypto only accepts arrays backed by an ArrayBuffer, so results are typed as such.
export type Bytes = Uint8Array<ArrayBuffer>

export type CryptoErrorCode = 'malformed' | 'unsupported' | 'authentication'

// Failures of the encryption code. The message never holds key material, plaintext or ciphertext.
export class CryptoError extends Error {
  readonly code: CryptoErrorCode

  constructor(code: CryptoErrorCode, message: string) {
    super(message)
    this.name = 'CryptoError'
    this.code = code
  }
}

export function bytesOf(source: Uint8Array): Bytes {
  return source.buffer instanceof ArrayBuffer && source.byteOffset === 0 && source.byteLength === source.buffer.byteLength
    ? (source as Bytes)
    : new Uint8Array(source)
}

export function concat(...parts: Uint8Array[]): Bytes {
  const out = new Uint8Array(parts.reduce((total, part) => total + part.length, 0))
  let offset = 0
  for (const part of parts) {
    out.set(part, offset)
    offset += part.length
  }
  return out
}

export function u16(value: number): Bytes {
  if (!Number.isInteger(value) || value < 0 || value > 0xffff) throw new CryptoError('malformed', 'Value does not fit in 16 bits')
  const out = new Uint8Array(2)
  new DataView(out.buffer).setUint16(0, value)
  return out
}

export function u32(value: number): Bytes {
  if (!Number.isInteger(value) || value < 0 || value > 0xffffffff) throw new CryptoError('malformed', 'Value does not fit in 32 bits')
  const out = new Uint8Array(4)
  new DataView(out.buffer).setUint32(0, value)
  return out
}

export function readU16(bytes: Uint8Array, offset: number): number {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint16(offset)
}

export function readU32(bytes: Uint8Array, offset: number): number {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset)
}

const encoder = new TextEncoder()
const decoder = new TextDecoder('utf-8', { fatal: true })

export function utf8(text: string): Bytes {
  return encoder.encode(text)
}

export function fromUtf8(bytes: Uint8Array): string {
  try {
    return decoder.decode(bytes)
  } catch {
    throw new CryptoError('malformed', 'Text is not valid UTF-8')
  }
}

export function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
}

export function fromHex(hex: string): Bytes {
  if (hex.length % 2 !== 0 || !/^[0-9a-fA-F]*$/.test(hex)) throw new CryptoError('malformed', 'Not a hex string')
  const out = new Uint8Array(hex.length / 2)
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16)
  return out
}

export function toBase64Url(bytes: Uint8Array): string {
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function fromBase64Url(text: string): Bytes {
  if (!/^[A-Za-z0-9_-]*$/.test(text) || text.length % 4 === 1) throw new CryptoError('malformed', 'Not base64url')
  const padded = text.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(text.length / 4) * 4, '=')
  const binary = atob(padded)
  const out = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i)
  return out
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

// The 16 bytes of a UUID written as 8-4-4-4-12 hex digits, in that order.
export function uuidBytes(id: string): Bytes {
  if (!UUID.test(id)) throw new CryptoError('malformed', 'Not a UUID')
  return fromHex(id.replace(/-/g, ''))
}

export function uuidFromBytes(bytes: Uint8Array): string {
  if (bytes.length !== 16) throw new CryptoError('malformed', 'A UUID is 16 bytes')
  const hex = toHex(bytes)
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

// Compares without stopping at the first difference, for values an attacker may try to guess
// byte by byte (commitments, check codes).
export function equal(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false
  let difference = 0
  for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i]
  return difference === 0
}

export function compare(a: Uint8Array, b: Uint8Array): number {
  const shared = Math.min(a.length, b.length)
  for (let i = 0; i < shared; i++) {
    if (a[i] !== b[i]) return a[i] - b[i]
  }
  return a.length - b.length
}
