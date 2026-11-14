// One attachment, as the app uses the file format: the bytes of a file go in and come out as the file the bucket stores and an entry for
// the sealed message, and the other way round. The entry carries what the server is never told (the name, the type, the size) and the key
// that opens the file; the stored file carries nothing but chunks (see file.ts). Whole files in memory: a file is at most a few
// megabytes, and the chunked layout is there so a reader can fetch and open any part of a larger one.

import { CryptoError, fromBase64Url, toBase64Url, type Bytes } from './bytes'
import { FILE_CHUNK_BYTES, type FileOverrides, decryptFile, encryptFile, encryptedFileLength, newFileKey, readFileHeader } from './file'
import type { FileRef } from './message'

export interface SealedAttachment {
  // What goes to the bucket.
  stored: Bytes
  // What goes into the sealed message.
  ref: FileRef
}

// Encrypts a file under a key made for it. `name` and `type` are written into the entry as they are given: making a name harmless is the
// business of the app, which does it on the way in and again on the way out.
export async function sealAttachment(
  file: { name: string; type: string },
  plain: Uint8Array,
  overrides: FileOverrides = {},
): Promise<SealedAttachment> {
  const key = newFileKey()
  const stored = await encryptFile(key, plain, overrides)

  return {
    stored,
    ref: { name: file.name, type: file.type, size: plain.length, key: toBase64Url(key), chunkSize: overrides.chunkSize ?? FILE_CHUNK_BYTES },
  }
}

// The bytes of the file an entry describes, from the file as stored. The stored file must be the one the entry says it is, in the length
// and the way it was cut, before anything is decrypted: an entry paired with another file, or a file cut off or extended, is refused here
// and not by whatever fails first.
export async function openAttachment(ref: FileRef, stored: Uint8Array): Promise<Bytes> {
  if (stored.length !== encryptedFileLength(ref.size, ref.chunkSize)) throw new CryptoError('malformed', 'The file is not the size the message says')
  if (readFileHeader(stored).chunkSize !== ref.chunkSize) throw new CryptoError('malformed', 'The file is not cut as the message says')

  return decryptFile(fromBase64Url(ref.key), stored)
}
