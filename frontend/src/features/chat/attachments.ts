import { formatFileSize } from '../../lib/format'

// The size and the count the server enforces (Storage in the API configuration), repeated here so that a file that cannot be sent is
// refused before it is encrypted and uploaded. The server stays the authority for these two and answers with a message of its own if this
// copy has drifted. The size is that of the file as a person sees it: what is stored for it is a little larger (a header, and a tag for
// every 64 KiB), which the server allows for.
export const MAX_FILE_BYTES = 10 * 1024 * 1024
export const MAX_FILES_PER_MESSAGE = 4

// What kinds of file a message may carry is decided here and nowhere else: the server cannot see what a file is, because it is encrypted.

export const ALLOWED_CONTENT_TYPES: readonly string[] = [
  'image/jpeg',
  'image/png',
  'image/gif',
  'image/webp',
  'video/mp4',
  'video/webm',
  'application/pdf',
  'text/plain',
  'application/zip',
]

// What the file dialog offers. A browser may still let someone pick another type, which is checked below.
export const FILE_PICKER_ACCEPT = ALLOWED_CONTENT_TYPES.join(',')

export interface FileDetails {
  name: string
  size: number
  type: string
}

// Why a file cannot be attached, or null when it can. `attached` is how many files the message already has.
export function checkFile(file: FileDetails, attached: number): string | null {
  if (attached >= MAX_FILES_PER_MESSAGE) return `A message can have at most ${MAX_FILES_PER_MESSAGE} files.`
  if (!ALLOWED_CONTENT_TYPES.includes(file.type)) return `${file.name}: this type of file cannot be sent.`
  if (file.size === 0) return `${file.name} is empty.`
  if (file.size > MAX_FILE_BYTES) return `${file.name} is larger than ${formatFileSize(MAX_FILE_BYTES)}.`

  return null
}

// What the conversation list shows for a last message that has files: next to its text, or instead of it
// when there is none.
export function attachmentPreview(count: number): string {
  return count === 1 ? 'Attachment' : `${count} attachments`
}
