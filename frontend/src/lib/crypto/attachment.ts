// One attachment, as the app uses the file format: a file goes in and comes out as the file the bucket stores and an entry for the sealed
// message, and the other way round. The entry carries what the server is never told (the name, the type, the size) and the key that opens
// the file; the stored file carries nothing but chunks (see file.ts). Files are Blobs and are worked on a slice of chunks at a time, so a
// file of the largest size (100 MiB) is never held whole in the page's memory, going up or coming down.

import { CryptoError, fromBase64Url, toBase64Url } from './bytes'
import { FILE_CHUNK_BYTES, type FileOverrides, decryptFileBlob, encryptFileBlob, encryptedFileLength, newFileKey, readBlobHeader } from './file'
import type { FileRef } from './message'

export interface SealedAttachment {
  // What goes to the bucket.
  stored: Blob
  // What goes into the sealed message.
  ref: FileRef
}

// Encrypts a file under a key made for it. `name` and `type` are written into the entry as they are given: making a name harmless is the
// business of the app, which does it on the way in and again on the way out.
export async function sealAttachment(
  file: { name: string; type: string },
  plain: Blob,
  overrides: FileOverrides = {},
): Promise<SealedAttachment> {
  const key = newFileKey()
  const stored = await encryptFileBlob(key, plain, overrides)

  return {
    stored,
    ref: { name: file.name, type: file.type, size: plain.size, key: toBase64Url(key), chunkSize: overrides.chunkSize ?? FILE_CHUNK_BYTES },
  }
}

// The file an entry describes, from the file as stored. The stored file must be the one the entry says it is, in the length
// and the way it was cut, before anything is decrypted: an entry paired with another file, or a file cut off or extended, is refused here
// and not by whatever fails first.
export async function openAttachment(ref: FileRef, stored: Blob): Promise<Blob> {
  if (stored.size !== encryptedFileLength(ref.size, ref.chunkSize)) throw new CryptoError('malformed', 'The file is not the size the message says')
  if ((await readBlobHeader(stored)).chunkSize !== ref.chunkSize) throw new CryptoError('malformed', 'The file is not cut as the message says')

  return decryptFileBlob(fromBase64Url(ref.key), stored)
}
