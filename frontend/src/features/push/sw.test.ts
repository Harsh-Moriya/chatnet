import { describe, expect, it, vi } from 'vitest'
import source from '../../../public/sw.js?raw'

// The service worker is a plain script the browser runs on its own, so it is loaded here the way the browser would: the file as
// it is, run against a stand-in for the worker scope, and given the events the browser gives it.

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

function loadWorker(windows: FakeWindow[] = []) {
  const listeners = new Map<string, (event: unknown) => void>()
  const scope = {
    location: { origin: ORIGIN },
    addEventListener: (type: string, listener: (event: unknown) => void) => listeners.set(type, listener),
    skipWaiting: vi.fn(),
    clients: {
      claim: vi.fn(async () => undefined),
      matchAll: vi.fn(async () => windows),
      openWindow: vi.fn(async () => null),
    },
    registration: { showNotification: vi.fn(async () => undefined) },
  }

  new Function('self', source)(scope)

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

  return { scope, listeners, dispatch, push, click }
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
      data: { conversationId: CONVERSATION, messageId: MESSAGE },
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

  it('shows an alert without a tag as one that replaces nothing', async () => {
    const worker = loadWorker()

    await worker.push({ ...alert, tag: undefined })

    expect(worker.scope.registration.showNotification).toHaveBeenCalledWith(
      'Alice in Team',
      expect.objectContaining({ tag: undefined }),
    )
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
    expect([...loadWorker().listeners.keys()].sort()).toEqual(['activate', 'install', 'notificationclick', 'push'])
  })
})
