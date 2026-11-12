import { refreshAttachmentUrl } from '../api/chatApi'
import { cleanFileName } from './fileNames'
import { type FileSource, createFileObjects } from './fileObjects'

// The files of the messages on screen, opened for real: fetched from the bucket (which lets the app's address read it), opened with the
// cryptography when the first one is needed, and handed to the page as object URLs.
const loadFileCrypto = () => import('../../../lib/crypto/attachment')

// The bytes of a file come out of decryption in memory of their own, never shared, which is what a Blob takes.
const blobOf = (bytes: Uint8Array, type: string) => new Blob([bytes as Uint8Array<ArrayBuffer>], { type })

export const fileObjects = createFileObjects({
  fetchStored: async (url) => {
    const response = await fetch(url, { credentials: 'omit' })
    if (!response.ok) throw new Error(`The storage answered ${response.status}.`)
    return new Uint8Array(await response.arrayBuffer())
  },
  renewLink: (file) => refreshAttachmentUrl(file.conversationId, file.messageId, file.id),
  open: async (entry, stored) => (await loadFileCrypto()).openAttachment(entry, stored),
  makeUrl: (bytes, type) => URL.createObjectURL(blobOf(bytes, type)),
  revokeUrl: (url) => URL.revokeObjectURL(url),
})

// Saves a file to the person's computer under its own name. What the sender says the file is does not matter here: the bytes are saved as
// bytes the browser is not asked to show, so a file that says it is a page is not opened as one.
export async function saveFile(file: FileSource): Promise<void> {
  const bytes = await fileObjects.read(file)
  const address = URL.createObjectURL(blobOf(bytes, 'application/octet-stream'))

  const link = document.createElement('a')
  link.href = address
  link.download = cleanFileName(file.entry.name)
  link.rel = 'noopener'
  document.body.append(link)
  link.click()
  link.remove()

  // The download has been handed to the browser by the time the click returns, but a browser that is slow to start it still needs the address.
  setTimeout(() => URL.revokeObjectURL(address), 60_000)
}
