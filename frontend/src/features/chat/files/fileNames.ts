import type { FileRef } from '../../../lib/crypto/message'

// What a file says it is is written by its sender and sealed in the message, and the server never sees it. Everything here decides what a
// reader does with those claims: they are made harmless before they are shown, and only what is known to be safe to draw is drawn.

export type AttachmentKind = 'Image' | 'Video' | 'File'

// The longest name a file is shown with, in UTF-16 code units (the same the sealed entry allows).
const MAX_NAME_LENGTH = 255

// What a file is called once it is shown or saved. The name comes from the sender's computer and is shown to other people and written into
// the name of a download, so it is made harmless first:
//   - only the last part of a path is kept ("C:\docs\plan.pdf" is "plan.pdf")
//   - control characters are removed, and so are the invisible formatting characters (the right-to-left override among them), which are
//     how "photo.exe" is made to look like "photo.gpj"
//   - nothing left means "file"
export function cleanFileName(name: string): string {
  const lastPart = name.slice(Math.max(name.lastIndexOf('/'), name.lastIndexOf('\\')) + 1)
  const cleaned = lastPart.replace(/[\p{Cc}\p{Cf}]/gu, '').trim().slice(0, MAX_NAME_LENGTH).trim()

  return cleaned === '' ? 'file' : cleaned
}

// The pictures that are drawn in a message. SVG is not among them: it can carry script, and the bytes of a picture are claimed by its sender.
const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

// The videos that are played in a message.
const VIDEO_TYPES = ['video/mp4', 'video/webm']

// The type without parameters and in lower case: "Image/PNG; charset=x" is "image/png".
function normalized(type: string): string {
  const parameters = type.indexOf(';')
  return (parameters < 0 ? type : type.slice(0, parameters)).trim().toLowerCase()
}

// The type to give the bytes of a picture, or null when what the sender says it is is not one that is drawn.
export function imageTypeOf(type: string): string | null {
  const known = normalized(type)
  return IMAGE_TYPES.includes(known) ? known : null
}

// The type to give the bytes of a video, or null when it is not one that is played.
export function videoTypeOf(type: string): string | null {
  const known = normalized(type)
  return VIDEO_TYPES.includes(known) ? known : null
}

// How a file is shown: a picture drawn in the message, a video played in it, or anything else as a file to save.
export function kindOf(file: Pick<FileRef, 'type'>): AttachmentKind {
  if (imageTypeOf(file.type) !== null) return 'Image'
  if (videoTypeOf(file.type) !== null) return 'Video'
  return 'File'
}
