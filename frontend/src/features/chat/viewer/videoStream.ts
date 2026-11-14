import { fromBase64Url } from '../../../lib/crypto/bytes'
import { encryptedFileLength } from '../../../lib/crypto/file'
import { type NeedReply, type OpenReply, type RenewReply, type StreamDescriptor, type WorkerRequest, streamUrl } from '../../../sw/protocol'
import { videoTypeOf } from '../files/fileNames'
import type { FileSource } from '../files/fileObjects'

// The page's side of playing a video from the service worker: it tells the worker which file (and with what key) a stream is for, and
// answers the two things the worker may ask later: the stream again, when the browser stopped the worker and it lost it, and a new link, when
// the one it was given has expired. Where there is no worker to play from, `open` gives null and the caller plays the file another way.

// How long the worker has to say it took a stream.
const ACK_TIMEOUT_MS = 3000

// What the page has to do this with, so that it is tested without a browser.
export interface StreamHost {
  // The worker that controls this page, or null: a browser without service workers, one that refused, or a page loaded before the worker took over.
  controller: () => { postMessage: (message: unknown, transfer: Transferable[]) => void } | null
  // Messages from the worker, with the port each is to be answered on. Returns the way to stop listening.
  listen: (listener: (message: { data: unknown; ports: readonly { postMessage: (reply: unknown) => void }[] }) => void) => () => void
  renewLink: (file: FileSource) => Promise<string>
  newId: () => string
}

export interface VideoStream {
  // The address a <video> plays from.
  url: string
  // Lets the worker forget the stream and its key.
  stop: () => void
}

interface Kept {
  stream: StreamDescriptor
  source: FileSource
}

function asRequest(data: unknown): WorkerRequest | null {
  if (typeof data !== 'object' || data === null) return null

  const { type, id } = data as { type?: unknown; id?: unknown }
  return (type === 'stream-need' || type === 'stream-renew') && typeof id === 'string' ? { type, id } : null
}

export function createVideoStreams(host: StreamHost) {
  // The streams this page has opened, which the worker may ask about again.
  const kept = new Map<string, Kept>()
  let stopListening: (() => void) | null = null

  function answer(request: WorkerRequest, port: { postMessage: (reply: unknown) => void } | undefined) {
    const entry = kept.get(request.id)

    if (request.type === 'stream-need') {
      port?.postMessage((entry?.stream ?? null) satisfies NeedReply)
      return
    }

    if (!entry) {
      port?.postMessage(null satisfies RenewReply)
      return
    }
    host.renewLink(entry.source).then(
      (url) => {
        // The new link is kept with the stream, so that the worker is given it if it asks for the stream again.
        entry.stream.url = url
        port?.postMessage({ url } satisfies RenewReply)
      },
      () => port?.postMessage(null satisfies RenewReply),
    )
  }

  function listen() {
    stopListening ??= host.listen((message) => {
      const request = asRequest(message.data)
      if (request) answer(request, message.ports[0])
    })
  }

  function forget(id: string) {
    kept.delete(id)
    if (kept.size === 0) {
      stopListening?.()
      stopListening = null
    }
  }

  // Whether the worker took the stream: it says so on a channel made for the answer, or does not say in time.
  function opened(controller: NonNullable<ReturnType<StreamHost['controller']>>, stream: StreamDescriptor): Promise<boolean> {
    return new Promise((resolve) => {
      const channel = new MessageChannel()
      const timer = setTimeout(() => {
        channel.port1.close()
        resolve(false)
      }, ACK_TIMEOUT_MS)

      channel.port1.onmessage = (event: MessageEvent) => {
        clearTimeout(timer)
        channel.port1.close()
        resolve((event.data as OpenReply | null)?.ok === true)
      }
      controller.postMessage({ type: 'stream-open', stream }, [channel.port2])
    })
  }

  return {
    async open(source: FileSource): Promise<VideoStream | null> {
      const controller = host.controller()
      const contentType = videoTypeOf(source.entry.type)
      if (!controller || !contentType) return null

      const id = host.newId()
      const stream: StreamDescriptor = {
        id,
        key: fromBase64Url(source.entry.key),
        chunkSize: source.entry.chunkSize,
        size: source.entry.size,
        storedSize: encryptedFileLength(source.entry.size, source.entry.chunkSize),
        url: source.url,
        contentType,
      }

      // Kept before the worker is told, so that a question from it can be answered as soon as it has the stream.
      kept.set(id, { stream, source })
      listen()

      if (!(await opened(controller, stream))) {
        forget(id)
        return null
      }

      let stopped = false
      return {
        url: streamUrl(id),
        stop: () => {
          if (stopped) return
          stopped = true
          forget(id)
          host.controller()?.postMessage({ type: 'stream-close', id }, [])
        },
      }
    },
  }
}
