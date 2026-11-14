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

// The header of a new file, with the pieces every chunk of it is sealed with.
function startFile(overrides: FileOverrides) {
  const chunkSize = overrides.chunkSize ?? FILE_CHUNK_BYTES
  if (!Number.isInteger(chunkSize) || chunkSize < 1 || chunkSize > MAX_CHUNK_BYTES) throw new CryptoError('malformed', 'Chunk size is out of range')

  const header = { chunkSize, noncePrefix: overrides.noncePrefix ? new Uint8Array(overrides.noncePrefix) : randomBytes(NONCE_PREFIX_BYTES) }
  const headerBytes = encodeHeader(header)
  return { chunkSize, header, headerBytes, aad: labelled(LABELS.fileChunk, headerBytes) }
}

export async function encryptFile(key: Uint8Array, plain: Uint8Array, overrides: FileOverrides = {}): Promise<Bytes> {
  const { chunkSize, header, headerBytes, aad } = startFile(overrides)
  const count = Math.max(1, Math.ceil(plain.length / chunkSize))

  const parts: Uint8Array[] = [headerBytes]
  for (let index = 0; index < count; index++) {
    const chunk = plain.subarray(index * chunkSize, (index + 1) * chunkSize)
    parts.push(await aesGcmEncrypt(key, chunkNonce(header.noncePrefix, index, index === count - 1), chunk, aad))
  }
  return concat(...parts)
}

// How much of a file is read into memory at a time when it is worked on as a Blob: a slice of whole chunks of about this size.
const SLICE_BYTES = 4 * 1024 * 1024

const chunksPerSlice = (chunkSize: number) => Math.max(1, Math.floor(SLICE_BYTES / chunkSize))

// The same stored file as encryptFile makes, from a Blob and as a Blob, a slice of chunks at a time. A file of 100 MiB is never in memory
// whole: only the slice being sealed is, and the sealed slices are joined as Blobs, which the browser may keep outside the page's memory.
export async function encryptFileBlob(key: Uint8Array, plain: Blob, overrides: FileOverrides = {}): Promise<Blob> {
  const { chunkSize, header, headerBytes, aad } = startFile(overrides)
  const count = Math.max(1, Math.ceil(plain.size / chunkSize))
  const perSlice = chunksPerSlice(chunkSize)

  const slices: BlobPart[] = [headerBytes]
  for (let first = 0; first < count; first += perSlice) {
    const end = Math.min(count, first + perSlice)
    const bytes = new Uint8Array(await plain.slice(first * chunkSize, end * chunkSize).arrayBuffer())

    const sealed: Bytes[] = []
    for (let index = first; index < end; index++) {
      const chunk = bytes.subarray((index - first) * chunkSize, (index - first + 1) * chunkSize)
      sealed.push(await aesGcmEncrypt(key, chunkNonce(header.noncePrefix, index, index === count - 1), chunk, aad))
    }
    slices.push(new Blob(sealed))
  }
  return new Blob(slices)
}

// One chunk, as stored (ciphertext and tag). The caller says whether it is the last, which it
// knows from the size of the stored file; a wrong answer fails to authenticate.
export function decryptChunk(key: Uint8Array, header: FileHeader, index: number, isLast: boolean, chunk: Uint8Array): Promise<Bytes> {
  return aesGcmDecrypt(key, chunkNonce(header.noncePrefix, index, isLast), chunk, labelled(LABELS.fileChunk, encodeHeader(header)))
}

// How many chunks a stored file of this length has.
function chunkCount(header: FileHeader, storedLength: number): number {
  const body = storedLength - FILE_HEADER_BYTES
  if (body < TAG_BYTES) throw new CryptoError('malformed', 'File has no chunks')

  // A last chunk shorter than its tag is refused by the decryption of that chunk.
  return Math.ceil(body / (header.chunkSize + TAG_BYTES))
}

export async function decryptFile(key: Uint8Array, stored: Uint8Array): Promise<Bytes> {
  const header = readFileHeader(stored)
  const stride = header.chunkSize + TAG_BYTES
  const count = chunkCount(header, stored.length)

  const parts: Uint8Array[] = []
  for (let index = 0; index < count; index++) {
    const start = chunkOffset(header, index)
    parts.push(await decryptChunk(key, header, index, index === count - 1, stored.subarray(start, Math.min(start + stride, stored.length))))
  }
  return concat(...parts)
}

export async function readBlobHeader(stored: Blob): Promise<FileHeader> {
  return readFileHeader(new Uint8Array(await stored.slice(0, FILE_HEADER_BYTES).arrayBuffer()))
}

// The file inside a stored file, both as Blobs, opened a slice of chunks at a time like encryptFileBlob seals them.
export async function decryptFileBlob(key: Uint8Array, stored: Blob): Promise<Blob> {
  const header = await readBlobHeader(stored)
  const stride = header.chunkSize + TAG_BYTES
  const count = chunkCount(header, stored.size)
  const perSlice = chunksPerSlice(header.chunkSize)

  const slices: BlobPart[] = []
  for (let first = 0; first < count; first += perSlice) {
    const end = Math.min(count, first + perSlice)
    const bytes = new Uint8Array(await stored.slice(chunkOffset(header, first), Math.min(chunkOffset(header, end), stored.size)).arrayBuffer())

    const opened: Bytes[] = []
    for (let index = first; index < end; index++) {
      const start = (index - first) * stride
      opened.push(await decryptChunk(key, header, index, index === count - 1, bytes.subarray(start, start + stride)))
    }
    slices.push(new Blob(opened))
  }
  return new Blob(slices)
}
