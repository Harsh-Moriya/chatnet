import { afterEach, describe, expect, it, vi } from 'vitest'
import { encryptFile, newFileKey } from '../lib/crypto/file'
import type { StreamDescriptor } from './protocol'
import type { StreamDeps } from './streams'
import { type WorkerScope, installWorker } from './worker'

// The service worker is installed here into a stand-in for the worker scope, and given the events the browser gives it.

const CONVERSATION = '7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'
const MESSAGE = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d'
const ORIGIN = 'https://chat.example'

interface FakeWindow {
  url: string
  visibilityState: 'visible' | 'hidden'
  focused: boolean
  focus: ReturnType<typeof vi.fn>
  postMessage: ReturnType<typeof vi.fn>
}

function aWindow(overrides: Partial<FakeWindow> = {}): FakeWindow {
  return {
    url: `${ORIGIN}/chat/someone`,
    visibilityState: 'hidden',
    focused: false,
    focus: vi.fn(async () => undefined),
    postMessage: vi.fn(),
    ...overrides,
  }
}

function loadWorker(windows: FakeWindow[] = [], deps?: StreamDeps) {
  const listeners = new Map<string, (event: unknown) => void>()
  const scope = {
    location: { origin: ORIGIN },
    addEventListener: (type: string, listener: (event: unknown) => void) => listeners.set(type, listener),
    skipWaiting: vi.fn(),
    clients: {
      claim: vi.fn(async () => undefined),
      matchAll: vi.fn(async () => windows),
      get: vi.fn(async (id: string) => windows.find((window) => (window as FakeWindow & { id?: string }).id === id)),
      openWindow: vi.fn(async () => null),
    },
    registration: { showNotification: vi.fn(async () => undefined) },
  }

  installWorker(scope as unknown as WorkerScope, deps)

  // Runs a handler the way the browser does: the work it hands to waitUntil is what keeps the worker alive, so it is awaited.
  async function dispatch(type: string, event: object) {
    const pending: Promise<unknown>[] = []
    const handler = listeners.get(type)
    if (!handler) throw new Error(`The worker does not listen for ${type}.`)

    handler({ ...event, waitUntil: (work: Promise<unknown>) => pending.push(work) })
    await Promise.all(pending)
  }

  const push = (data: unknown) => dispatch('push', { data: { json: () => data } })
  const click = (data: unknown) => {
    const notification = { data, close: vi.fn() }
    return dispatch('notificationclick', { notification }).then(() => notification)
  }

  // A request for a file, the way the browser gives it to the worker: what is handed to respondWith is what the page gets, and a worker that
  // does not answer leaves it out.
  function fetched(url: string, headers: Record<string, string> = {}, clientId = '') {
    let answer: Promise<Response> | null = null
    const handler = listeners.get('fetch')
    if (!handler) throw new Error('The worker does not listen for fetch.')

    handler({ request: new Request(url, { headers }), clientId, respondWith: (response: Promise<Response>) => (answer = response) })
    return answer as Promise<Response> | null
  }

  // A message from the page, with the port the worker answers on.
  async function message(data: unknown) {
    const channel = new MessageChannel()
    const reply = new Promise<unknown>((resolve) => {
      channel.port1.onmessage = (event) => resolve(event.data)
      setTimeout(() => resolve(undefined), 50)
    })
    listeners.get('message')?.({ data, ports: [channel.port2] })
    const answered = await reply
    channel.port1.close()
    channel.port2.close()
    return answered
  }

  return { scope, listeners, dispatch, push, click, fetched, message }
}

const alert = {
  title: 'Alice in Team',
  body: 'see you at 5',
  tag: `conversation-${CONVERSATION}`,
  type: 'message',
  conversationId: CONVERSATION,
  messageId: MESSAGE,
}

describe('showing a push', () => {
  it('shows the title and the words with the tag, and keeps the ids for the click', async () => {
    const worker = loadWorker()

    await worker.push(alert)

    expect(worker.scope.registration.showNotification).toHaveBeenCalledExactlyOnceWith('Alice in Team', {
      body: 'see you at 5',
      tag: `conversation-${CONVERSATION}`,
      renotify: true,
      data: { conversationId: CONVERSATION, messageId: MESSAGE },
      icon: '/icons/icon-192.png',
      badge: '/icons/badge-96.png',
      vibrate: expect.any(Array),
      timestamp: expect.any(Number),
    })
  })

  it('shows it while the application is open but hidden, or open in a window that is not in front', async () => {
    const worker = loadWorker([
      aWindow({ visibilityState: 'visible', focused: false }),
      aWindow({ visibilityState: 'hidden', focused: true }),
    ])

    await worker.push(alert)

    expect(worker.scope.registration.showNotification).toHaveBeenCalledOnce()
  })

  it('shows nothing while the person is looking at the application, which shows it itself', async () => {
    const worker = loadWorker([aWindow({ visibilityState: 'visible', focused: true })])

    await worker.push(alert)

    expect(worker.scope.registration.showNotification).not.toHaveBeenCalled()
  })

  it('looks at the windows it does not control too, such as one opened before the worker took over', async () => {
    const worker = loadWorker()

    await worker.push(alert)

    expect(worker.scope.clients.matchAll).toHaveBeenCalledWith({ type: 'window', includeUncontrolled: true })
  })

  it('keeps the ids out of the notification when they are not ids, because they become an address', async () => {
    const worker = loadWorker()

    await worker.push({ ...alert, conversationId: '../../admin', messageId: 'javascript:alert(1)' })

    expect(worker.scope.registration.showNotification).toHaveBeenCalledWith(
      'Alice in Team',
      expect.objectContaining({ data: { conversationId: null, messageId: null } }),
    )
  })

  it('keeps a call that is ringing on the screen until it is dealt with, and no other kind of alert', async () => {
    const worker = loadWorker()

    await worker.push({ ...alert, title: 'Bob', body: 'Incoming audio call', tag: 'call-k1', type: 'call', messageId: undefined })
    await worker.push({ ...alert, type: 'missed-call', tag: 'call-k1', body: 'Missed audio call' })

    const [ringing, missed] = worker.scope.registration.showNotification.mock.calls as unknown as [string, Record<string, unknown>][]
    expect(ringing[1]).toMatchObject({ tag: 'call-k1', requireInteraction: true })
    expect(missed[1]).not.toHaveProperty('requireInteraction')
  })

  it('shows an alert without a tag as one that replaces nothing', async () => {
    const worker = loadWorker()

    await worker.push({ ...alert, tag: undefined })

    const [, options] = worker.scope.registration.showNotification.mock.calls[0] as unknown as [string, Record<string, unknown>]
    expect(options).not.toHaveProperty('tag')
    expect(options).not.toHaveProperty('renotify')
  })

  it('shows an alert with no words at all as the title alone', async () => {
    const worker = loadWorker()

    await worker.push({ ...alert, body: undefined })

    expect(worker.scope.registration.showNotification).toHaveBeenCalledWith(
      'Alice in Team',
      expect.objectContaining({ body: '' }),
    )
  })

  it.each([
    ['text that is not JSON', () => { throw new Error('not json') }],
    ['nothing', () => null],
    ['a number', () => 42],
    ['a push without a title', () => ({ body: 'x' })],
    ['a title that is not text', () => ({ title: 7 })],
  ])('still shows an alert for %s, because a browser withdraws the permission of a site that shows nothing', async (_, json) => {
    const worker = loadWorker()

    await worker.dispatch('push', { data: { json } })

    expect(worker.scope.registration.showNotification).toHaveBeenCalledExactlyOnceWith('ChatNET', {
      body: 'Something new is waiting for you.',
      tag: 'chatnet',
      icon: '/icons/icon-192.png',
      badge: '/icons/badge-96.png',
    })
  })

  it('shows an alert for a push that carries no data at all', async () => {
    const worker = loadWorker()

    await worker.dispatch('push', { data: null })

    expect(worker.scope.registration.showNotification).toHaveBeenCalledWith('ChatNET', expect.anything())
  })

  it('keeps the worker alive until the alert is shown', async () => {
    const worker = loadWorker()
    let shown = false
    worker.scope.registration.showNotification.mockImplementation(async () => {
      await Promise.resolve()
      shown = true
    })

    await worker.push(alert)

    expect(shown).toBe(true)
  })
})

describe('clicking an alert', () => {
  it('closes the alert', async () => {
    const worker = loadWorker()

    const notification = await worker.click({ conversationId: CONVERSATION, messageId: null })

    expect(notification.close).toHaveBeenCalledOnce()
  })

  it('brings the open application forward and tells it where to go, without opening another window', async () => {
    const open = aWindow()
    const worker = loadWorker([open])

    await worker.click({ conversationId: CONVERSATION, messageId: MESSAGE })

    expect(open.focus).toHaveBeenCalledOnce()
    expect(open.postMessage).toHaveBeenCalledExactlyOnceWith({
      type: 'open-conversation',
      conversationId: CONVERSATION,
      messageId: MESSAGE,
    })
    expect(worker.scope.clients.openWindow).not.toHaveBeenCalled()
  })

  it('opens the conversation in a new window when the application is not open', async () => {
    const worker = loadWorker()

    await worker.click({ conversationId: CONVERSATION, messageId: MESSAGE })

    expect(worker.scope.clients.openWindow).toHaveBeenCalledExactlyOnceWith(`/chat/${CONVERSATION}`)
  })

  it('does not take a window of another site for the application', async () => {
    const stranger = aWindow({ url: 'https://elsewhere.example/chat/x' })
    const worker = loadWorker([stranger])

    await worker.click({ conversationId: CONVERSATION, messageId: null })

    expect(stranger.focus).not.toHaveBeenCalled()
    expect(worker.scope.clients.openWindow).toHaveBeenCalledWith(`/chat/${CONVERSATION}`)
  })

  it('opens the front page when the alert leads nowhere, and never an address made of something that is not an id', async () => {
    const worker = loadWorker()

    await worker.click({ conversationId: '../../admin' })
    await worker.click(undefined)
    await worker.click(null)

    expect(worker.scope.clients.openWindow.mock.calls).toEqual([['/'], ['/'], ['/']])
  })

  it('brings the application forward without moving it when the alert leads nowhere', async () => {
    const open = aWindow()
    const worker = loadWorker([open])

    await worker.click({ conversationId: null, messageId: null })

    expect(open.focus).toHaveBeenCalledOnce()
    expect(open.postMessage).not.toHaveBeenCalled()
  })

  it('leaves the message out of what it tells the application when the alert had none', async () => {
    const open = aWindow()
    const worker = loadWorker([open])

    await worker.click({ conversationId: CONVERSATION, messageId: 'nope' })

    expect(open.postMessage).toHaveBeenCalledWith({
      type: 'open-conversation',
      conversationId: CONVERSATION,
      messageId: null,
    })
  })
})

describe('the life of the worker', () => {
  it('takes over at once when it is installed and claims the open pages when it is activated', async () => {
    const worker = loadWorker()

    await worker.dispatch('install', {})
    await worker.dispatch('activate', {})

    expect(worker.scope.skipWaiting).toHaveBeenCalledOnce()
    expect(worker.scope.clients.claim).toHaveBeenCalledOnce()
  })

  it('listens for exactly the events it handles', () => {
    expect([...loadWorker().listeners.keys()].sort()).toEqual(['activate', 'fetch', 'install', 'message', 'notificationclick', 'push'])
  })
})

describe('playing a video', () => {
  const STREAM = '7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'
  const URL_OF = `${ORIGIN}/file-stream/${STREAM}`

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  async function video(length = 300) {
    const key = newFileKey()
    const plain = Uint8Array.from({ length }, (_, i) => (i * 7 + 1) % 251)
    const stored = await encryptFile(key, plain, { chunkSize: 64 })
    const stream: StreamDescriptor = { id: STREAM, key, chunkSize: 64, size: length, storedSize: stored.length, url: 'https://bucket.test/f?sig=1', contentType: 'video/webm' }
    const fetchRange = vi.fn(async (_url: string, from: number, to: number) => new Response(stored.slice(from, to + 1), { status: 206 }))
    const deps: StreamDeps = { fetchRange, askPageForStream: vi.fn(async () => null), askPageForLink: vi.fn(async () => null) }
    return { plain, stored, stream, fetchRange, deps }
  }

  // A bucket as `fetch` meets it: the range is in a header, and the answer is those bytes of the object.
  const bucketOf = (object: Uint8Array) => async (_url: string, init: RequestInit) => {
    const [from, to] = /bytes=(\d+)-(\d+)/.exec(String((init.headers as Record<string, string>).Range))!.slice(1).map(Number)
    return new Response(object.slice(from, to + 1), { status: 206 })
  }

  const bodyOf = async (response: Promise<Response> | null) => new Uint8Array(await (await response!).arrayBuffer())

  it('answers a request for a stream of its own site, and leaves every other request to the network', async () => {
    const worker = loadWorker()

    expect(worker.fetched(`${ORIGIN}/api/conversations`)).toBeNull()
    expect(worker.fetched(`${ORIGIN}/assets/index.js`)).toBeNull()
    expect(worker.fetched(`${ORIGIN}/file-stream/not-an-id`)).toBeNull()
    expect(worker.fetched(`https://elsewhere.example/file-stream/${STREAM}`)).toBeNull()
    expect(worker.fetched(URL_OF)).not.toBeNull()
  })

  it('plays the stream the page opened, and says it took it', async () => {
    const file = await video()
    const worker = loadWorker([], file.deps)

    const reply = await worker.message({ type: 'stream-open', stream: file.stream })
    const response = worker.fetched(URL_OF, { Range: 'bytes=70-79' })

    expect(reply).toEqual({ ok: true })
    expect(Array.from(await bodyOf(response))).toEqual(Array.from(file.plain.subarray(70, 80)))
  })

  it('says it did not take a stream that is not usable, and then has none to play', async () => {
    const file = await video()
    const worker = loadWorker([], file.deps)

    const reply = await worker.message({ type: 'stream-open', stream: { ...file.stream, contentType: 'text/html' } })
    const response = await worker.fetched(URL_OF, { Range: 'bytes=0-9' })

    expect(reply).toEqual({ ok: false })
    expect(response!.status).toBe(404)
  })

  it.each([undefined, null, 'text', 42])('says it did not take a stream when the message carries none: %j', async (stream) => {
    const worker = loadWorker([], (await video()).deps)

    expect(await worker.message({ type: 'stream-open', stream })).toEqual({ ok: false })
  })

  it.each([null, 'text', 42, {}, { type: 'something-else' }])('ignores a message it does not understand: %j', async (data) => {
    const worker = loadWorker([], (await video()).deps)

    expect(await worker.message(data)).toBeUndefined()
  })

  it('forgets a stream the page closes: the next request asks the page, which still has it', async () => {
    const file = await video()
    file.deps.askPageForStream = vi.fn(async () => file.stream)
    const worker = loadWorker([], file.deps)

    await worker.message({ type: 'stream-open', stream: file.stream })
    await worker.message({ type: 'stream-close', id: STREAM })
    const response = worker.fetched(URL_OF, { Range: 'bytes=0-9' })

    expect(Array.from(await bodyOf(response))).toEqual(Array.from(file.plain.subarray(0, 10)))
    expect(file.deps.askPageForStream).toHaveBeenCalledOnce()
  })

  describe('with the page asked for real, on a channel made for the question', () => {
    // A window that answers what the worker asks the way the page does.
    function page(answer: (request: { type: string; id: string }) => unknown) {
      const window = aWindow() as FakeWindow & { id: string }
      window.id = 'page-1'
      window.postMessage = vi.fn((request: unknown, transfer?: Transferable[]) => {
        const port = transfer![0] as MessagePort
        port.postMessage(answer(request as { type: string; id: string }))
        port.close()
      })
      return window
    }

    it('gets back a stream it lost when the browser stopped it, from the page that made the request', async () => {
      const file = await video()
      vi.stubGlobal('fetch', bucketOf(file.stored))
      const window = page((request) => (request.type === 'stream-need' ? file.stream : null))
      const worker = loadWorker([window])

      const response = worker.fetched(URL_OF, { Range: 'bytes=100-120' }, 'page-1')

      expect(Array.from(await bodyOf(response))).toEqual(Array.from(file.plain.subarray(100, 121)))
      expect(window.postMessage).toHaveBeenCalledWith({ type: 'stream-need', id: STREAM }, expect.any(Array))
    })

    it('asks the page for a new link when the bucket refuses the old one, and plays on', async () => {
      const file = await video()
      const answer = bucketOf(file.stored)
      vi.stubGlobal('fetch', vi.fn(async (url: string, init: RequestInit) => (String(url).includes('sig=1') ? new Response(null, { status: 403 }) : answer(url, init))))
      const window = page((request) => (request.type === 'stream-need' ? file.stream : { url: 'https://bucket.test/f?sig=2' }))
      const worker = loadWorker([window])

      const response = worker.fetched(URL_OF, { Range: 'bytes=0-9' }, 'page-1')

      expect(Array.from(await bodyOf(response))).toEqual(Array.from(file.plain.subarray(0, 10)))
      expect(window.postMessage).toHaveBeenCalledWith({ type: 'stream-renew', id: STREAM }, expect.any(Array))
    })

    it('sends the range to the bucket as a header, without the credentials of the site', async () => {
      const file = await video()
      const bucket = vi.fn(bucketOf(file.stored))
      vi.stubGlobal('fetch', bucket)
      const worker = loadWorker([page(() => file.stream)])

      await bodyOf(worker.fetched(URL_OF, { Range: 'bytes=0-9' }, 'page-1'))

      expect(bucket).toHaveBeenCalledWith('https://bucket.test/f?sig=1', expect.objectContaining({ credentials: 'omit', headers: { Range: expect.stringMatching(/^bytes=\d+-\d+$/) } }))
    })

    it('has nothing to play, and says so, when no page answers', async () => {
      vi.useFakeTimers()
      try {
        const window = aWindow() as FakeWindow & { id: string }
        window.id = 'page-1'
        window.postMessage = vi.fn()
        const worker = loadWorker([window])

        const response = worker.fetched(URL_OF, { Range: 'bytes=0-9' }, 'page-1')
        await vi.advanceTimersByTimeAsync(6000)

        expect((await response!).status).toBe(404)
      } finally {
        vi.useRealTimers()
      }
    })
  })
})
