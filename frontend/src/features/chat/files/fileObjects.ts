import type { FileRef } from '../../../lib/crypto/message'
import { imageTypeOf, videoTypeOf } from './fileNames'

// A file of a message is encrypted in the bucket, so showing it means fetching the encrypted bytes, opening them with the entry of the
// sealed message, and handing the browser an object URL for the result. This keeps the files that are on screen, and nothing else: each
// is opened once however many places ask, and the ones nobody is showing are let go when there are too many.

// Where the work is done. Injected, so the rules here are tested without a browser, a network or the cryptography.
export interface FileObjectDeps {
  // The encrypted file behind a link. Throws when the link does not give it.
  fetchStored: (url: string) => Promise<Uint8Array>
  // A new link for the file, for when the one held has expired. Returns the address.
  renewLink: (file: FileSource) => Promise<string>
  // The bytes of the file, from the encrypted file and the entry that describes it. Throws when they do not go together.
  open: (entry: FileRef, stored: Uint8Array) => Promise<Uint8Array>
  makeUrl: (bytes: Uint8Array, type: string) => string
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

// What is shown at most at once without anyone looking at it: the files not held by a visible message are kept for this many, newest first.
const KEPT_UNUSED = 24

interface Held {
  url: Promise<string>
  type: string
  // How many places are showing it. A file that is being shown is never let go.
  holders: number
  lastUsed: number
}

export interface FileObjects {
  // The object URL of a picture or a video, opened once. Resolves to null for a file that is not drawn (any other type), without fetching
  // anything. The caller takes a hold and gives it back with `release` when it stops showing the file.
  hold: (file: FileSource) => Promise<string | null>
  release: (file: FileSource) => void
  // The bytes of a file, for saving. Not kept.
  read: (file: FileSource) => Promise<Uint8Array>
  dispose: () => void
}

export function createFileObjects(deps: FileObjectDeps, now: () => number = Date.now): FileObjects {
  const held = new Map<string, Held>()

  // The encrypted file, from the link held and, when that does not give it, from a new one asked for once.
  async function load(file: FileSource): Promise<Uint8Array> {
    let stored: Uint8Array
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
    return imageTypeOf(entry.type) ?? videoTypeOf(entry.type)
  }

  // Lets go of the files nobody is showing, oldest first, beyond what is kept.
  function trim() {
    const idle = [...held.entries()].filter(([, entry]) => entry.holders === 0).sort((a, b) => a[1].lastUsed - b[1].lastUsed)

    for (const [id, entry] of idle.slice(0, Math.max(0, idle.length - KEPT_UNUSED))) {
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
        const url = load(file).then((bytes) => deps.makeUrl(bytes, type))
        entry = { url, type, holders: 0, lastUsed: now() }
        held.set(file.id, entry)

        // A failure is not kept: the next to ask tries again.
        url.catch(() => {
          if (held.get(file.id) === entry) held.delete(file.id)
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
