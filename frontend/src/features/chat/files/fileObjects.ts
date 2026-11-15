import type { FileRef } from '../../../lib/crypto/message'
import { imageTypeOf, pdfTypeOf, videoTypeOf } from './fileNames'

// A file of a message is encrypted in the bucket, so showing it means fetching the encrypted file, opening it with the entry of the
// sealed message, and handing the browser an object URL for the result. Files are Blobs here, so one of the largest size (100 MiB) is not held
// in the page's memory whole. This keeps the files that are on screen, and little else: each is opened once however many places ask, and the
// ones nobody is showing are let go when they add up to more than a budget of bytes.

// Where the work is done. Injected, so the rules here are tested without a browser, a network or the cryptography.
export interface FileObjectDeps {
  // The encrypted file behind a link. Throws when the link does not give it.
  fetchStored: (url: string) => Promise<Blob>
  // A new link for the file, for when the one held has expired. Returns the address.
  renewLink: (file: FileSource) => Promise<string>
  // The file, from the encrypted file and the entry that describes it. Throws when they do not go together.
  open: (entry: FileRef, stored: Blob) => Promise<Blob>
  makeUrl: (file: Blob, type: string) => string
  revokeUrl: (url: string) => void
}

// What is needed to find and open one file.
export interface FileSource {
  id: string
  conversationId: string
  messageId: string
  url: string
  entry: FileRef
}

// The file could not be fetched, even with a new link.
export class FileUnavailableError extends Error {
  constructor() {
    super('The file could not be fetched.')
    this.name = 'FileUnavailableError'
  }
}

// The file was fetched and is not the one the message describes, or has been changed.
export class FileInvalidError extends Error {
  constructor() {
    super('The file is not the one the message describes.')
    this.name = 'FileInvalidError'
  }
}

// What is kept of the files nobody is showing, newest first, so that showing one again does not fetch it again: up to this many bytes.
const KEPT_UNUSED_BYTES = 64 * 1024 * 1024

interface Held {
  url: Promise<string>
  // How large the opened file is; 0 until it has been opened.
  size: number
  type: string
  // How many places are showing it. A file that is being shown is never let go.
  holders: number
  lastUsed: number
}

export interface FileObjects {
  // The object URL of a picture, a video or a PDF, opened once. Resolves to null for a file that is not shown (any other type), without
  // fetching anything. The caller takes a hold and gives it back with `release` when it stops showing the file. The address of a PDF goes
  // to the PDF reader and nowhere else: never to a frame, a link or an embed, which would hand the file to the browser's own viewer.
  hold: (file: FileSource) => Promise<string | null>
  release: (file: FileSource) => void
  // A file, for saving. Not kept.
  read: (file: FileSource) => Promise<Blob>
  dispose: () => void
}

export function createFileObjects(deps: FileObjectDeps, now: () => number = Date.now): FileObjects {
  const held = new Map<string, Held>()

  // The encrypted file, from the link held and, when that does not give it, from a new one asked for once.
  async function load(file: FileSource): Promise<Blob> {
    let stored: Blob
    try {
      stored = await deps.fetchStored(file.url)
    } catch {
      let renewed: string
      try {
        renewed = await deps.renewLink(file)
        stored = await deps.fetchStored(renewed)
      } catch {
        throw new FileUnavailableError()
      }
    }

    try {
      return await deps.open(file.entry, stored)
    } catch {
      throw new FileInvalidError()
    }
  }

  function typeOf(entry: FileRef): string | null {
    return imageTypeOf(entry.type) ?? videoTypeOf(entry.type) ?? pdfTypeOf(entry.type)
  }

  // Lets go of the files nobody is showing, oldest first, once together they are more than the budget: the newest are kept while they fit.
  function trim() {
    const idle = [...held.entries()].filter(([, entry]) => entry.holders === 0).sort((a, b) => b[1].lastUsed - a[1].lastUsed)

    // A file that is let go of takes no room, so a large one does not cost the older files that would fit without it.
    let kept = 0
    for (const [id, entry] of idle) {
      if (kept + entry.size <= KEPT_UNUSED_BYTES) {
        kept += entry.size
        continue
      }

      held.delete(id)
      entry.url.then(deps.revokeUrl, () => undefined)
    }
  }

  return {
    async hold(file) {
      const type = typeOf(file.entry)
      if (type === null) return null

      let entry = held.get(file.id)
      if (!entry) {
        const created: Held = { url: Promise.resolve(''), size: 0, type, holders: 0, lastUsed: now() }
        created.url = load(file).then((opened) => {
          created.size = opened.size
          return deps.makeUrl(opened, type)
        })
        entry = created
        held.set(file.id, entry)

        // A failure is not kept: the next to ask tries again.
        created.url.catch(() => {
          if (held.get(file.id) === created) held.delete(file.id)
        })
      }

      entry.holders++
      entry.lastUsed = now()
      return entry.url
    },

    release(file) {
      const entry = held.get(file.id)
      if (!entry) return

      entry.holders = Math.max(0, entry.holders - 1)
      entry.lastUsed = now()
      trim()
    },

    read: (file) => load(file),

    dispose() {
      for (const entry of held.values()) entry.url.then(deps.revokeUrl, () => undefined)
      held.clear()
    },
  }
}
