// An attachment as the bucket stores it: encrypted in fixed-size chunks, so a client can fetch and
// decrypt any part of a large file (a video seeks) without reading the rest.
//
//   version(1) | suite(1) | chunk size(4) | nonce prefix(7) | chunk 0 | chunk 1 | ...
//
// Each chunk is AES-256-GCM of up to chunk size bytes of the file plus a 16-byte tag. The nonce of
// a chunk is the nonce prefix, the chunk index (4 bytes) and a final flag (1 byte, 1 on the last
// chunk only), and the associated data is the file-chunk label and the 13-byte header. Binding the
// position and the end into the nonce means chunks cannot be reordered, dropped from the end, or
// spliced between files.
// The key is random per file and travels inside the encrypted message, never beside the file.

import { CryptoError, concat, readU32, u32, type Bytes } from './bytes'
import { KEY_BYTES, LABELS, SUITE_V1, TAG_BYTES, VERSION, labelled } from './formats'
import { aesGcmDecrypt, aesGcmEncrypt, randomBytes } from './primitives'

export const FILE_CHUNK_BYTES = 64 * 1024
export const MAX_CHUNK_BYTES = 16 * 1024 * 1024
export const FILE_HEADER_BYTES = 13
const NONCE_PREFIX_BYTES = 7

export interface FileHeader {
  chunkSize: number
  noncePrefix: Bytes
}

export function newFileKey(): Bytes {
  return randomBytes(KEY_BYTES)
}

function encodeHeader(header: FileHeader): Bytes {
  return concat(Uint8Array.of(VERSION, SUITE_V1), u32(header.chunkSize), header.noncePrefix)
}

export function readFileHeader(bytes: Uint8Array): FileHeader {
  if (bytes.length < FILE_HEADER_BYTES) throw new CryptoError('malformed', 'File is too short')
  if (bytes[0] !== VERSION || bytes[1] !== SUITE_V1) throw new CryptoError('unsupported', 'Unknown file version')
  const chunkSize = readU32(bytes, 2)
  if (chunkSize < 1 || chunkSize > MAX_CHUNK_BYTES) throw new CryptoError('malformed', 'Chunk size is out of range')
  return { chunkSize, noncePrefix: new Uint8Array(bytes.subarray(6, FILE_HEADER_BYTES)) }
}

function chunkNonce(prefix: Uint8Array, index: number, isLast: boolean): Bytes {
  return concat(prefix, u32(index), Uint8Array.of(isLast ? 1 : 0))
}

// Where chunk number index starts in the stored file, and how long a full chunk is there.
export function chunkOffset(header: FileHeader, index: number): number {
  return FILE_HEADER_BYTES + index * (header.chunkSize + TAG_BYTES)
}

export function encryptedFileLength(plainLength: number, chunkSize = FILE_CHUNK_BYTES): number {
  return FILE_HEADER_BYTES + plainLength + Math.max(1, Math.ceil(plainLength / chunkSize)) * TAG_BYTES
}

// Fixed inputs for the published test vectors. Real use leaves them out.
export interface FileOverrides {
  noncePrefix?: Uint8Array
  chunkSize?: number
}

export async function encryptFile(key: Uint8Array, plain: Uint8Array, overrides: FileOverrides = {}): Promise<Bytes> {
  const chunkSize = overrides.chunkSize ?? FILE_CHUNK_BYTES
  if (!Number.isInteger(chunkSize) || chunkSize < 1 || chunkSize > MAX_CHUNK_BYTES) throw new CryptoError('malformed', 'Chunk size is out of range')

  const header = { chunkSize, noncePrefix: overrides.noncePrefix ? new Uint8Array(overrides.noncePrefix) : randomBytes(NONCE_PREFIX_BYTES) }
  const headerBytes = encodeHeader(header)
  const aad = labelled(LABELS.fileChunk, headerBytes)
  const count = Math.max(1, Math.ceil(plain.length / chunkSize))

  const parts: Uint8Array[] = [headerBytes]
  for (let index = 0; index < count; index++) {
    const chunk = plain.subarray(index * chunkSize, (index + 1) * chunkSize)
    parts.push(await aesGcmEncrypt(key, chunkNonce(header.noncePrefix, index, index === count - 1), chunk, aad))
  }
  return concat(...parts)
}

// One chunk, as stored (ciphertext and tag). The caller says whether it is the last, which it
// knows from the size of the stored file; a wrong answer fails to authenticate.
export function decryptChunk(key: Uint8Array, header: FileHeader, index: number, isLast: boolean, chunk: Uint8Array): Promise<Bytes> {
  return aesGcmDecrypt(key, chunkNonce(header.noncePrefix, index, isLast), chunk, labelled(LABELS.fileChunk, encodeHeader(header)))
}

export async function decryptFile(key: Uint8Array, stored: Uint8Array): Promise<Bytes> {
  const header = readFileHeader(stored)
  const stride = header.chunkSize + TAG_BYTES
  const body = stored.length - FILE_HEADER_BYTES
  if (body < TAG_BYTES) throw new CryptoError('malformed', 'File has no chunks')

  // A last chunk shorter than its tag is refused by the decryption of that chunk.
  const count = Math.ceil(body / stride)

  const parts: Uint8Array[] = []
  for (let index = 0; index < count; index++) {
    const start = chunkOffset(header, index)
    parts.push(await decryptChunk(key, header, index, index === count - 1, stored.subarray(start, Math.min(start + stride, stored.length))))
  }
  return concat(...parts)
}
