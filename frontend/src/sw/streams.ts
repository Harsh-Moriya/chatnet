import { concat } from '../lib/crypto/bytes'
import { type FileHeader, MAX_CHUNK_BYTES, decryptChunk, encryptedFileLength, readFileHeader, FILE_HEADER_BYTES } from '../lib/crypto/file'
import { KEY_BYTES, TAG_BYTES } from '../lib/crypto/formats'
import { type StreamDescriptor, streamIdOf, streamUrl } from './protocol'
import { answerHeaders, capRange, chunkSpan, parseRange, unsatisfiableHeaders } from './range'

// Playing a video from an encrypted file in the bucket, a range at a time. A player asks for bytes of the file (/file-stream/<id>, with a
// Range header); the worker fetches the chunks that hold them from the bucket, opens each with the key of the file, and answers with the
// bytes asked for. The key is in the worker's memory only, and no byte of the file is ever answered that did not authenticate.

// How many streams the worker keeps. A person plays one at a time; the oldest is let go of when there are more.
export const MAX_STREAMS = 8

const VIDEO_TYPES = ['video/mp4', 'video/webm']

interface Entry {
  stream: StreamDescriptor
  // The 13 bytes at the start of the stored file, read once, when the first range is asked for.
  header: FileHeader | null
}

export interface StreamRegistry {
  // Keeps a stream and says whether it was acceptable.
  open: (stream: StreamDescriptor) => boolean
  close: (id: string) => void
  get: (id: string) => Entry | undefined
}

// What the worker is willing to play: a key of the right length, sizes that agree with each other (the stored size is what a file of that size
// is sealed to), a type a video may have, and a link to the web. A page that is not the application's cannot reach the worker, but a
// descriptor that does not agree with itself would only fail later and further away. A message that carries no descriptor at all is
// refused like any other that is not usable, and answered, so the page does not wait for a reply that never comes.
function isUsable(stream: StreamDescriptor): boolean {
  return (
    typeof stream === 'object' &&
    stream !== null &&
    streamIdOf(streamUrl(stream.id)) === stream.id &&
    stream.key instanceof Uint8Array &&
    stream.key.length === KEY_BYTES &&
    Number.isInteger(stream.chunkSize) &&
    stream.chunkSize >= 1 &&
    stream.chunkSize <= MAX_CHUNK_BYTES &&
    Number.isInteger(stream.size) &&
    stream.size > 0 &&
    stream.storedSize === encryptedFileLength(stream.size, stream.chunkSize) &&
    VIDEO_TYPES.includes(stream.contentType) &&
    /^https?:\/\//i.test(stream.url)
  )
}

export function createRegistry(): StreamRegistry {
  // Kept in the order they were last used, the oldest first.
  const entries = new Map<string, Entry>()

  return {
    open(stream) {
      if (!isUsable(stream)) return false

      entries.delete(stream.id)
      entries.set(stream.id, { stream: { ...stream, key: new Uint8Array(stream.key) }, header: null })
      for (const oldest of entries.keys()) {
        if (entries.size <= MAX_STREAMS) break
        entries.delete(oldest)
      }
      return true
    },

    close(id) {
      entries.delete(id)
    },

    get(id) {
      const entry = entries.get(id)
      if (entry) {
        entries.delete(id)
        entries.set(id, entry)
      }
      return entry
    },
  }
}

export interface StreamDeps {
  // The bytes `from` to `to` (both included) of the object at a link. A bucket that does not honour a range answers 200, which is refused.
  fetchRange: (url: string, from: number, to: number) => Promise<Response>
  // The page that asked for the request knows the stream and the link: these ask it, and give null when it cannot answer.
  askPageForStream: (clientId: string, id: string) => Promise<StreamDescriptor | null>
  askPageForLink: (clientId: string, id: string) => Promise<string | null>
}

// A failure to answer, whatever its cause: the bucket, the link, the file. Its words never reach the page.
class StreamError extends Error {}

// The stored bytes `from` to `to`, exactly that many. A link the bucket refuses is asked for anew, once.
async function fetchStored(entry: Entry, from: number, to: number, clientId: string, deps: StreamDeps): Promise<Uint8Array> {
  let response = await deps.fetchRange(entry.stream.url, from, to)

  if (response.status === 403) {
    const url = await deps.askPageForLink(clientId, entry.stream.id)
    if (url === null || !/^https?:\/\//i.test(url)) throw new StreamError('The link could not be renewed.')

    entry.stream.url = url
    response = await deps.fetchRange(url, from, to)
  }

  if (response.status !== 206) throw new StreamError('The bucket did not answer the range.')

  const bytes = new Uint8Array(await response.arrayBuffer())
  if (bytes.length !== to - from + 1) throw new StreamError('The bucket answered another range.')
  return bytes
}

async function readHeader(entry: Entry, clientId: string, deps: StreamDeps): Promise<FileHeader> {
  if (entry.header) return entry.header

  // A file that is not cut as the page said is not checked here: its chunks are then in other places than the worker looks for them, and
  // none of them authenticates.
  entry.header = readFileHeader(await fetchStored(entry, 0, FILE_HEADER_BYTES - 1, clientId, deps))
  return entry.header
}

const failure = (status: number) => new Response(null, { status, headers: { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' } })

// The answer to a request for a stream: the part of the file asked for, opened, or a refusal.
export async function handleStreamRequest(request: Request, clientId: string, registry: StreamRegistry, deps: StreamDeps): Promise<Response> {
  const id = streamIdOf(new URL(request.url).pathname)
  if (id === null) return failure(404)

  let entry = registry.get(id)
  if (!entry) {
    // The browser stops a worker that has been idle, and what it kept goes with it. The page still has the stream.
    const stream = await deps.askPageForStream(clientId, id)
    if (stream !== null && registry.open(stream)) entry = registry.get(id)
  }
  if (!entry) return failure(404)

  const { stream } = entry
  const asked = parseRange(request.headers.get('range'), stream.size)
  if (asked === 'unsatisfiable') return new Response(null, { status: 416, headers: unsatisfiableHeaders(stream.size) })

  const range = capRange(asked)
  const span = chunkSpan(range, stream.chunkSize, stream.size)

  try {
    const header = await readHeader(entry, clientId, deps)
    const stored = await fetchStored(entry, span.storedFrom, span.storedTo, clientId, deps)
    const stride = stream.chunkSize + TAG_BYTES

    const chunks = Array.from({ length: span.last - span.first + 1 }, (_, offset) => {
      const index = span.first + offset
      return decryptChunk(stream.key, header, index, index === span.lastIndex, stored.subarray(offset * stride, (offset + 1) * stride))
    })
    const opened = concat(...(await Promise.all(chunks)))

    return new Response(opened.subarray(span.skip, span.skip + (range.end - range.start + 1)), { status: 206, headers: answerHeaders(range, stream) })
  } catch {
    // Nothing of a file that did not authenticate, or that could not be read whole, is given to the player.
    return failure(502)
  }
}
