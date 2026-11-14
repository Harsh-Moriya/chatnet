// What the page and the service worker say to each other about a stream: a video that the worker plays from the encrypted file, a range at a
// time. Shared by both sides, so it holds nothing but shapes, the address, and the one rule about what an address may be.

// The address a stream is played from: /file-stream/<id>. The worker answers requests for it and for nothing else.
export const STREAM_PATH = '/file-stream/'

const STREAM_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export const streamUrl = (id: string): string => `${STREAM_PATH}${id}`

// The id of the stream an address is for, or null when it is for none: a path that does not start with the stream path, or has anything but
// an id after it.
export function streamIdOf(pathname: string): string | null {
  if (!pathname.startsWith(STREAM_PATH)) return null

  const id = pathname.slice(STREAM_PATH.length)
  return STREAM_ID.test(id) ? id : null
}

// Everything the worker needs to play one file: the key that opens it, how it is cut, how large it is as it was and as it is stored, where
// the bucket holds it, and what to call it. The key travels in a message, never in an address, and the worker keeps it in memory only.
export interface StreamDescriptor {
  id: string
  key: Uint8Array
  chunkSize: number
  // The size of the file as it was, in bytes.
  size: number
  // The size of the encrypted file in the bucket.
  storedSize: number
  url: string
  // The type to answer with: one of the types a video may have.
  contentType: string
}

// From the page to the worker.
export type PageMessage = { type: 'stream-open'; stream: StreamDescriptor } | { type: 'stream-close'; id: string }

// From the worker to the page, each with a port to answer on. `stream-need`: the worker was stopped by the browser and has lost the stream,
// so the page sends it again (a descriptor, or null if it has let go of it). `stream-renew`: the link has expired and the page answers with
// a new one (`{ url }`, or null).
export type WorkerRequest = { type: 'stream-need'; id: string } | { type: 'stream-renew'; id: string }

export type OpenReply = { ok: boolean }
export type NeedReply = StreamDescriptor | null
export type RenewReply = { url: string } | null
