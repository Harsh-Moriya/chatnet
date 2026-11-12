import axios from 'axios'
import { api } from '../../../lib/api'
import { cleanFileName } from '../files/fileNames'
import type { Uploaded } from '../uploads'

// Uploading a file is three steps. The file is encrypted here, under a key made for it that only goes into the sealed message. The API is
// asked for permission, and told only how large the encrypted file is, and gives a link that points straight at the storage bucket; and the
// encrypted file is sent there, without going through the API. The file's bytes never pass through the server, which is why a presigned link
// is used, and the server never learns what the file is or is called.
const loadFileCrypto = () => import('../../../lib/crypto/attachment')

// The one content type every upload is signed for, whatever the file is.
const STORED_CONTENT_TYPE = 'application/octet-stream'

interface UploadTicket {
  // What the message names later to attach this file.
  uploadKey: string
  url: string
  expiresAt: string
}

// The failure of the second request. The storage answers with its own XML, not with the API's error format,
// so the message is written here instead of being taken from the response.
export class UploadFailed extends Error {
  constructor() {
    super('The file could not be uploaded. Check your connection and try again.')
  }
}

// Returns the key of the uploaded file and what the sealed message has to say about it. `onProgress` is called with how much of the upload has
// been sent, from 0 to 1. Aborting through `signal` ends it with the library's cancel error, which the caller is expected to ignore.
export async function uploadFile(
  conversationId: string,
  file: File,
  onProgress: (fraction: number) => void,
  signal: AbortSignal,
): Promise<Uploaded> {
  const { sealAttachment } = await loadFileCrypto()
  const { stored, ref } = await sealAttachment({ name: cleanFileName(file.name), type: file.type }, new Uint8Array(await file.arrayBuffer()))

  const { data: ticket } = await api.post<UploadTicket>(
    `/conversations/${conversationId}/uploads`,
    { sizeBytes: stored.length },
    { signal },
  )

  try {
    // The plain axios, not the API client: this goes to another host, and the API's token must not be sent
    // there. The content type is part of what the link was signed for, so it has to match.
    await axios.put(ticket.url, stored, {
      headers: { 'Content-Type': STORED_CONTENT_TYPE },
      signal,
      onUploadProgress: (event) => onProgress(event.total ? event.loaded / event.total : 0),
    })
  } catch (error) {
    if (axios.isCancel(error)) throw error
    throw new UploadFailed()
  }

  return { uploadKey: ticket.uploadKey, ref }
}

interface AttachmentLink {
  url: string
  expiresAt: string
}

// A fresh link for a file in a message the signed-in user can read. The links that come with a message last
// an hour; a tab that stays open longer finds them dead and asks for new ones.
export async function fetchAttachmentLink(attachmentId: string): Promise<AttachmentLink> {
  const { data } = await api.get<AttachmentLink>(`/attachments/${attachmentId}/url`)
  return data
}
