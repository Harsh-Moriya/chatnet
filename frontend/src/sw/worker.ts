import { type ClickEventLike, type PushEventLike, type PushScope, type WindowLike, openConversation, readAlert, showAlert } from './push'
import { type NeedReply, type OpenReply, type PageMessage, type RenewReply, type StreamDescriptor, type WorkerRequest, streamIdOf } from './protocol'
import { type StreamDeps, createRegistry, handleStreamRequest } from './streams'

// The service worker of ChatNET: which events it listens for and what each one does. It is installed into a scope that is handed in, so a
// test can give it a stand-in; the real one is main.ts, which hands it the worker's own scope.
//
// The worker is a classic script at a fixed address (/sw.js), fetched by the browser itself, which is why it is built apart from the page
// (see vite.sw.config.ts) and is not one of its chunks.

export interface WaitableEvent {
  waitUntil: (work: Promise<unknown>) => void
}

export interface FetchEventLike extends WaitableEvent {
  request: Request
  clientId: string
  respondWith: (response: Promise<Response>) => void
}

export interface MessageEventLike {
  data: unknown
  ports: readonly { postMessage: (message: unknown) => void }[]
}

export interface WorkerScope extends PushScope {
  addEventListener: <E>(type: string, listener: (event: E) => void) => void
  skipWaiting: () => unknown
  clients: PushScope['clients'] & { claim: () => Promise<void>; get: (id: string) => Promise<WindowLike | undefined> }
}

// How long the worker waits for the page to answer a question about a stream before it gives up on it.
const ASK_TIMEOUT_MS = 5000

// Asks one window a question and waits for its answer on a channel made for it, or for null.
function ask<T>(window: WindowLike, request: WorkerRequest): Promise<T | null> {
  return new Promise((resolve) => {
    const channel = new MessageChannel()
    const timer = setTimeout(() => {
      channel.port1.close()
      resolve(null)
    }, ASK_TIMEOUT_MS)

    channel.port1.onmessage = (event: MessageEvent) => {
      clearTimeout(timer)
      channel.port1.close()
      resolve(event.data as T | null)
    }
    window.postMessage(request, [channel.port2])
  })
}

// The real access to the bucket and to the page.
function streamDeps(scope: WorkerScope): StreamDeps {
  // The window that made the request, or, when the browser does not say, the windows the worker controls.
  async function pages(clientId: string): Promise<readonly WindowLike[]> {
    const direct = clientId === '' ? undefined : await scope.clients.get(clientId)
    return direct ? [direct] : scope.clients.matchAll({ type: 'window', includeUncontrolled: false })
  }

  async function askPages<T>(clientId: string, request: WorkerRequest): Promise<T | null> {
    for (const page of await pages(clientId)) {
      const answer = await ask<T>(page, request)
      if (answer !== null) return answer
    }
    return null
  }

  return {
    // The link is signed for the host, so a header for the range does not disturb it. Credentials are not sent to the bucket.
    fetchRange: (url, from, to) => fetch(url, { headers: { Range: `bytes=${from}-${to}` }, credentials: 'omit', cache: 'no-store' }),
    askPageForStream: (clientId, id) => askPages<NeedReply>(clientId, { type: 'stream-need', id }),
    askPageForLink: async (clientId, id) => (await askPages<RenewReply>(clientId, { type: 'stream-renew', id }))?.url ?? null,
  }
}

export function installWorker(scope: WorkerScope, deps: StreamDeps = streamDeps(scope)): void {
  // Take over at once when a new version is installed, so a fix to the worker does not wait for every tab to be closed.
  scope.addEventListener('install', () => {
    void scope.skipWaiting()
  })

  scope.addEventListener<WaitableEvent>('activate', (event) => {
    event.waitUntil(scope.clients.claim())
  })

  scope.addEventListener<PushEventLike & WaitableEvent>('push', (event) => {
    event.waitUntil(showAlert(scope, readAlert(event)))
  })

  scope.addEventListener<ClickEventLike & WaitableEvent>('notificationclick', (event) => {
    event.notification.close()
    event.waitUntil(openConversation(scope, event.notification.data))
  })

  // Playing encrypted video: the page opens a stream (a key and where the file is), and the worker answers the player's requests for it.
  const streams = createRegistry()

  scope.addEventListener<MessageEventLike>('message', (event) => {
    const message = event.data as PageMessage | null
    if (typeof message !== 'object' || message === null) return

    if (message.type === 'stream-open') {
      event.ports[0]?.postMessage({ ok: streams.open(message.stream as StreamDescriptor) } satisfies OpenReply)
    } else if (message.type === 'stream-close') {
      streams.close(message.id)
    }
  })

  // Only requests for a stream of this site are answered; everything else goes to the network as if the worker were not there.
  scope.addEventListener<FetchEventLike>('fetch', (event) => {
    const url = new URL(event.request.url)
    if (url.origin !== scope.location.origin || streamIdOf(url.pathname) === null) return

    event.respondWith(handleStreamRequest(event.request, event.clientId, streams, deps))
  })
}
