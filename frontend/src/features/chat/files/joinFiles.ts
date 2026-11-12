import { encryptedFileLength } from '../../../lib/crypto/file'
import type { FileRef } from '../../../lib/crypto/message'
import type { Attachment, AttachmentWire } from '../types'

// The server holds the files of a message and the sealed message says what they are, and the two are tied together by position: the n-th
// entry of the sealed message describes the n-th attachment. An attachment is shown as the file the entry says it is only when the entry
// agrees with what is stored, in the length an encrypted file of that size and chunk size has. An attachment the sealed message does not
// vouch for (it has no entry, the entry is for a file of another size, or the message has no files at all) gets no entry and is never
// drawn: nothing the server adds beside a sealed message is taken for part of it.
export function joinFiles(attachments: AttachmentWire[], entries: readonly FileRef[]): Attachment[] {
  return attachments.map((attachment, position) => {
    const entry = entries[position]
    const vouched = entry !== undefined && encryptedFileLength(entry.size, entry.chunkSize) === attachment.sizeBytes

    return { ...attachment, file: vouched ? entry : null }
  })
}

// An attachment of a message that could not be opened: nothing says what it is.
export function unvouched(attachments: AttachmentWire[]): Attachment[] {
  return attachments.map((attachment) => ({ ...attachment, file: null }))
}

// The entries of a message's files, to seal again with an edit, or null when one of them cannot be given: an entry that was not vouched
// for is not in the sealed message to carry forward, and leaving it out would shift the others out of place.
export function entriesOf(attachments: readonly Attachment[]): FileRef[] | null {
  const entries: FileRef[] = []
  for (const attachment of attachments) {
    if (attachment.file === null) return null
    entries.push(attachment.file)
  }
  return entries
}
