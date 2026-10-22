import { attachmentTypeOf, checkFile } from './attachments'
import type { PendingAttachment } from './types'

// The files being attached to the message that is being written, and what has become of each upload.
// Every function returns a new list, or the same one when nothing changed, so a component can use them in
// a state update. Plain data in and out, so the rules can be tested without a browser.

export type UploadStatus = 'uploading' | 'ready' | 'failed'

export interface UploadItem {
  id: string
  file: File
  status: UploadStatus
  // How much of the file has been sent, from 0 to 1.
  progress: number
  // Set once the upload has finished: what the message names to attach the file.
  uploadKey: string | null
  error: string | null
}

export interface AddResult {
  items: UploadItem[]
  added: UploadItem[]
  // One message for each file that was refused, to show the person.
  rejected: string[]
}

// Adds the files that may be attached, in order, and says why the others may not. Each accepted file counts
// towards the limit for the ones after it.
export function addFiles(items: UploadItem[], files: File[], newId: () => string): AddResult {
  const added: UploadItem[] = []
  const rejected: string[] = []

  for (const file of files) {
    const problem = checkFile(file, items.length + added.length)
    if (problem) {
      rejected.push(problem)
      continue
    }

    added.push({ id: newId(), file, status: 'uploading', progress: 0, uploadKey: null, error: null })
  }

  return { items: added.length > 0 ? [...items, ...added] : items, added, rejected }
}

function update(items: UploadItem[], id: string, change: (item: UploadItem) => UploadItem): UploadItem[] {
  const at = items.findIndex((item) => item.id === id)
  if (at === -1) return items

  const next = change(items[at])
  if (next === items[at]) return items

  return items.map((item, index) => (index === at ? next : item))
}

export function setProgress(items: UploadItem[], id: string, progress: number): UploadItem[] {
  return update(items, id, (item) =>
    // A late progress report must not move a finished or failed upload back.
    item.status === 'uploading' && item.progress !== progress ? { ...item, progress } : item,
  )
}

export function markReady(items: UploadItem[], id: string, uploadKey: string): UploadItem[] {
  return update(items, id, (item) =>
    item.status === 'uploading' ? { ...item, status: 'ready', progress: 1, uploadKey } : item,
  )
}

export function markFailed(items: UploadItem[], id: string, error: string): UploadItem[] {
  return update(items, id, (item) => (item.status === 'uploading' ? { ...item, status: 'failed', error } : item))
}

export function removeItem(items: UploadItem[], id: string): UploadItem[] {
  return items.some((item) => item.id === id) ? items.filter((item) => item.id !== id) : items
}

// A message can be sent when it says something or carries a file, no upload is still going, and none has
// failed: a file that failed has to be removed or sent again, never silently left out.
export function canSend(items: UploadItem[], text: string): boolean {
  if (items.some((item) => item.status !== 'ready')) return false

  return text.trim() !== '' || items.length > 0
}

export function toPendingAttachments(items: UploadItem[]): PendingAttachment[] {
  return items.flatMap((item) =>
    item.status === 'ready' && item.uploadKey !== null
      ? [
          {
            uploadKey: item.uploadKey,
            fileName: item.file.name,
            contentType: item.file.type,
            sizeBytes: item.file.size,
            type: attachmentTypeOf(item.file.type),
            file: item.file,
          },
        ]
      : [],
  )
}
