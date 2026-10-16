import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { connectChat, READINESS_PING, TYPING_TTL_MS, type ChatHub } from './chatConnection'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Message } from '../types'

const store = () => useChatStore.getState()

// ##### A stand-in for the SignalR connection #####
// It records what the code under test does to it and lets a test fire the events a real connection
// would raise (a pushed message, a dropped network). Nothing here talks to a server.

interface Deferred<T = void> {
  promise: Promise<T>
  resolve: (value: T) => void
  reject: (reason: unknown) => void
}

function deferred<T = void>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

class FakeHub implements ChatHub {
  readonly handlers = new Map<string, (...args: never[]) => void>()
  readonly calls: string[] = []
  readonly invocations: { method: string; args: unknown[] }[] = []
  startAttempts = 0
  stops = 0
  // One entry per start() call, in order: an Error makes that attempt fail, undefined lets it succeed.
  startOutcomes: (Error | undefined)[] = []
  // When set, start() waits for it, so a test can decide when (and how) the attempt ends.
  startGate: Deferred | null = null
  invokeImpl: (method: string, args: unknown[]) => Promise<unknown> = () => Promise.resolve()

  private reconnecting: (() => void)[] = []
  private reconnected: (() => void)[] = []
  private closed: (() => void)[] = []

  on(method: string, handler: (...args: never[]) => void): void {
    this.calls.push(`on:${method}`)
    this.handlers.set(method, handler)
  }

  onreconnecting(callback: () => void): void {
    this.reconnecting.push(callback)
  }

  onreconnected(callback: () => void): void {
    this.reconnected.push(callback)
  }

  onclose(callback: () => void): void {
    this.closed.push(callback)
  }

  async start(): Promise<void> {
    this.calls.push('start')
    this.startAttempts++
    if (this.startGate) await this.startGate.promise
    const outcome = this.startOutcomes.shift()
    if (outcome) throw outcome
  }

  async stop(): Promise<void> {
    this.stops++
    this.fireClosed()
  }

  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invocations.push({ method, args })
    return this.invokeImpl(method, args)
  }

  emit(method: string, ...args: unknown[]): unknown {
    const handler = this.handlers.get(method) as ((...a: unknown[]) => unknown) | undefined
    if (!handler) throw new Error(`No handler registered for ${method}`)
    return handler(...args)
  }

  fireReconnecting() {
    this.reconnecting.forEach((callback) => callback())
  }

  fireReconnected() {
    this.reconnected.forEach((callback) => callback())
  }

  fireClosed() {
    this.closed.forEach((callback) => callback())
  }

  invoked(method: string) {
    return this.invocations.filter((i) => i.method === method)
  }
}

function message(id: string, overrides: Partial<Message> = {}): Message {
  return {
    id,
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: 'hello',
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    ...overrides,
  }
}

function conversation(id: string): Conversation {
  return { id, type: 'Direct', name: 'Bob', avatarUrl: null, updatedAt: '2026-10-11T09:00:00Z', lastMessage: null, unreadCount: 0 }
}

const RETRY_DELAYS = [100, 200, 400]

function setup(options: { hub?: FakeHub; sessionExpired?: () => boolean } = {}) {
  const hub = options.hub ?? new FakeHub()
  const onReconnected = vi.fn()
  const onSessionExpired = vi.fn()
  const connection = connectChat(hub, {
    getUserId: () => 'me',
    onReconnected,
    isSessionExpired: options.sessionExpired ?? (() => false),
    onSessionExpired,
    retryDelay: (attempt) => RETRY_DELAYS[Math.min(attempt, RETRY_DELAYS.length - 1)],
  })
  return { hub, connection, onReconnected, onSessionExpired }
}

// Lets promise callbacks run, and any timers due now.
const flush = () => vi.advanceTimersByTimeAsync(0)

// Brings a connection fully up so a test can start from "connected".
async function connected() {
  const context = setup()
  await flush()
  expect(store().connection).toBe('connected')
  return context
}

beforeEach(() => {
  vi.useFakeTimers()
  store().reset()
})

afterEach(() => {
  vi.useRealTimers()
})

// ##### Handlers #####

describe('event handlers', () => {
  it('registers all handlers before it starts the connection', () => {
    const { hub } = setup()

    const start = hub.calls.indexOf('start')
    expect(start).toBeGreaterThan(-1)
    for (const event of ['ReceiveMessage', 'AddedToConversation', 'UserTyping', 'MessageRead']) {
      const registered = hub.calls.indexOf(`on:${event}`)
      expect(registered, event).toBeGreaterThan(-1)
      expect(registered, event).toBeLessThan(start)
    }
  })

  // The SignalR JavaScript client treats whatever a handler returns as a reply to the server and
  // logs an error when the server did not ask for one.
  it('returns nothing from any handler', async () => {
    const { hub } = await connected()
    hub.invokeImpl = () => Promise.resolve('a value the handler must not pass on')

    expect(hub.emit('ReceiveMessage', message('m1'))).toBeUndefined()
    expect(hub.emit('AddedToConversation', conversation('c9'))).toBeUndefined()
    expect(hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)).toBeUndefined()
    expect(hub.emit('MessageRead', 'c1', 'bob', '2026-10-11T10:00:00Z')).toBeUndefined()
  })

  it('puts a pushed message into the store', async () => {
    const { hub } = await connected()

    hub.emit('ReceiveMessage', message('m1'))

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m1'])
  })

  it('counts a pushed message from someone else as unread, and one from the signed-in user as read', async () => {
    const { hub } = await connected()
    store().seedConversations([{ ...conversation('c1'), unreadCount: 1 }])

    hub.emit('ReceiveMessage', message('m1', { senderId: 'bob', createdAt: '2026-10-11T10:00:01Z' }))
    expect(store().conversations.c1.unreadCount).toBe(2)

    hub.emit('ReceiveMessage', message('m2', { senderId: 'me', createdAt: '2026-10-11T10:00:02Z' }))
    expect(store().conversations.c1.unreadCount).toBe(0)
  })

  it('records who read a conversation, and lowers the signed-in user\x27s own count when they read in another tab', async () => {
    const { hub } = await connected()
    store().seedConversations([
      { ...conversation('c1'), unreadCount: 2, lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T10:00:05Z' } },
    ])
    store().seedMembers('c1', [
      { userId: 'me', displayName: 'Me', avatarUrl: null, role: 'Member', lastReadAt: null },
      { userId: 'bob', displayName: 'Bob', avatarUrl: null, role: 'Member', lastReadAt: null },
    ])

    hub.emit('MessageRead', 'c1', 'bob', '2026-10-11T10:00:04Z')
    expect(store().members.c1.find((m) => m.userId === 'bob')?.lastReadAt).toBe('2026-10-11T10:00:04Z')
    expect(store().conversations.c1.unreadCount).toBe(2)

    hub.emit('MessageRead', 'c1', 'me', '2026-10-11T10:00:05Z')
    expect(store().conversations.c1.unreadCount).toBe(0)
  })

  it('adds an announced conversation and joins it, which is the only way to receive its messages', async () => {
    const { hub } = await connected()

    hub.emit('AddedToConversation', conversation('c9'))

    expect(store().conversations.c9.name).toBe('Bob')
    expect(hub.invoked('JoinConversation')).toEqual([{ method: 'JoinConversation', args: ['c9'] }])
  })

  it('survives a refused join', async () => {
    const { hub } = await connected()
    hub.invokeImpl = (method) =>
      method === 'JoinConversation' ? Promise.reject(new Error('You are not a member of this conversation.')) : Promise.resolve()

    hub.emit('AddedToConversation', conversation('c9'))
    await flush()

    expect(store().conversations.c9).toBeDefined()
  })
})

// ##### Typing from other people #####

describe('typing events', () => {
  it('shows who is typing', async () => {
    const { hub } = await connected()

    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)

    expect(store().typing.c1).toEqual([{ userId: 'bob', displayName: 'Bob' }])
  })

  it('ignores the signed-in user, who also hears about their own typing from other tabs', async () => {
    const { hub } = await connected()

    hub.emit('UserTyping', 'c1', 'me', 'Me', true)

    expect(store().typing.c1).toBeUndefined()
  })

  it('stops showing someone who stops', async () => {
    const { hub } = await connected()
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)

    hub.emit('UserTyping', 'c1', 'bob', 'Bob', false)

    expect(store().typing.c1).toEqual([])
  })

  // The "stopped" event can be lost (a dropped connection, a closed tab), and without a timeout the
  // indicator would stay on for ever.
  it('stops showing someone after a while if the stop never arrives', async () => {
    const { hub } = await connected()
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)

    await vi.advanceTimersByTimeAsync(TYPING_TTL_MS - 1)
    expect(store().typing.c1).toHaveLength(1)

    await vi.advanceTimersByTimeAsync(1)
    expect(store().typing.c1).toEqual([])
  })

  it('keeps showing someone who keeps sending the signal', async () => {
    const { hub } = await connected()
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)

    await vi.advanceTimersByTimeAsync(TYPING_TTL_MS - 1000)
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)
    await vi.advanceTimersByTimeAsync(TYPING_TTL_MS - 1000)

    expect(store().typing.c1).toHaveLength(1)
  })

  it('times each person out on their own', async () => {
    const { hub } = await connected()
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)
    await vi.advanceTimersByTimeAsync(3000)
    hub.emit('UserTyping', 'c1', 'carol', 'Carol', true)

    await vi.advanceTimersByTimeAsync(TYPING_TTL_MS - 3000)

    expect(store().typing.c1.map((u) => u.userId)).toEqual(['carol'])
  })

  it('does not let an old timer cut short a newer typing session', async () => {
    const { hub } = await connected()
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', false)
    await vi.advanceTimersByTimeAsync(TYPING_TTL_MS - 1000)
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)

    await vi.advanceTimersByTimeAsync(2000)

    expect(store().typing.c1).toHaveLength(1)
  })
})

// ##### Becoming ready #####

describe('starting', () => {
  // start() can finish before the server has put the connection into its conversation groups, so a
  // message sent in that gap would be missed. One round trip to the server proves the groups are
  // joined, because the server handles a connection's calls only after its connect step is done.
  it('is not ready until the server has answered a round trip', async () => {
    const hub = new FakeHub()
    const ping = deferred()
    hub.invokeImpl = (method) => (method === READINESS_PING.method ? ping.promise : Promise.resolve())

    setup({ hub })
    await flush()
    expect(hub.invoked(READINESS_PING.method)).toEqual([READINESS_PING])
    expect(store().connection).toBe('connecting')

    ping.resolve()
    await flush()
    expect(store().connection).toBe('connected')
  })

  it('treats a failed round trip as ready, because the connection events will correct it if it dropped', async () => {
    const hub = new FakeHub()
    hub.invokeImpl = () => Promise.reject(new Error('connection lost'))

    setup({ hub })
    await flush()

    expect(store().connection).toBe('connected')
  })

  it('does not claim to be ready if the connection dropped during the round trip', async () => {
    const hub = new FakeHub()
    const ping = deferred()
    hub.invokeImpl = () => ping.promise
    setup({ hub })
    await flush()

    hub.fireReconnecting()
    ping.resolve()
    await flush()

    expect(store().connection).toBe('reconnecting')
  })

  it('keeps trying with growing delays when the server cannot be reached', async () => {
    const hub = new FakeHub()
    hub.startOutcomes = [new Error('down'), new Error('down'), undefined]

    setup({ hub })
    await flush()
    expect(hub.startAttempts).toBe(1)
    expect(store().connection).toBe('disconnected')

    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0] - 1)
    expect(hub.startAttempts).toBe(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(hub.startAttempts).toBe(2)

    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[1] - 1)
    expect(hub.startAttempts).toBe(2)
    await vi.advanceTimersByTimeAsync(1)
    expect(hub.startAttempts).toBe(3)

    expect(store().connection).toBe('connected')
  })

  it('stops trying once disposed', async () => {
    const hub = new FakeHub()
    hub.startOutcomes = [new Error('down'), new Error('down'), new Error('down')]
    const { connection } = setup({ hub })
    await flush()

    connection.dispose()
    await vi.advanceTimersByTimeAsync(60_000)

    expect(hub.startAttempts).toBe(1)
  })

  // React runs an effect, undoes it and runs it again in development, so a connection is often
  // abandoned while it is still starting. That must neither retry nor show an error.
  it('stays quiet when it is disposed while still starting', async () => {
    const hub = new FakeHub()
    hub.startGate = deferred()
    const { connection } = setup({ hub })
    await flush()

    connection.dispose()
    hub.startGate.reject(new Error('The connection was stopped during negotiation.'))
    await vi.advanceTimersByTimeAsync(60_000)

    expect(hub.startAttempts).toBe(1)
    expect(store().connection).toBe('connecting')
  })
})

// ##### Staying connected #####

describe('connection events', () => {
  it('shows reconnecting when the network drops', async () => {
    const { hub } = await connected()

    hub.fireReconnecting()

    expect(store().connection).toBe('reconnecting')
  })

  it('is ready again only after the round trip, and only then refreshes data', async () => {
    const { hub, onReconnected } = await connected()
    const statusWhenRefreshing: string[] = []
    onReconnected.mockImplementation(() => statusWhenRefreshing.push(store().connection))
    const ping = deferred()
    hub.invokeImpl = () => ping.promise
    hub.fireReconnecting()

    hub.fireReconnected()
    await flush()
    expect(store().connection).toBe('reconnecting')
    expect(onReconnected).not.toHaveBeenCalled()

    ping.resolve()
    await flush()
    expect(store().connection).toBe('connected')
    expect(onReconnected).toHaveBeenCalledTimes(1)
    // The refresh must run when requests are allowed to start, not before.
    expect(statusWhenRefreshing).toEqual(['connected'])
  })

  it('does not refresh data after the first connect, only after a reconnect', async () => {
    const { onReconnected } = await connected()

    expect(onReconnected).not.toHaveBeenCalled()
  })

  it('shows disconnected when the connection closes for good', async () => {
    const { hub } = await connected()

    hub.fireClosed()

    expect(store().connection).toBe('disconnected')
  })

  it('does not report its own shutdown as a lost connection', async () => {
    const { hub, connection } = await connected()

    connection.dispose()
    await flush()

    expect(hub.stops).toBe(1)
    expect(store().connection).toBe('connected')
  })
})

// ##### Sending typing signals #####

describe('sendTyping', () => {
  it('asks the server to announce typing and its end', async () => {
    const { hub, connection } = await connected()

    connection.sendTyping('c1', true)
    connection.sendTyping('c1', false)

    expect(hub.invoked('StartTyping')).toEqual([{ method: 'StartTyping', args: ['c1'] }])
    expect(hub.invoked('StopTyping').filter((i) => i.args[0] === 'c1')).toHaveLength(1)
  })

  it('does nothing while not connected', async () => {
    const { hub, connection } = await connected()
    hub.fireReconnecting()

    connection.sendTyping('c1', true)

    expect(hub.invoked('StartTyping')).toEqual([])
  })

  it('does nothing after being disposed', async () => {
    const { hub, connection } = await connected()
    connection.dispose()

    connection.sendTyping('c1', true)

    expect(hub.invoked('StartTyping')).toEqual([])
  })

  it('survives the server refusing', async () => {
    const { hub, connection } = await connected()
    hub.invokeImpl = () => Promise.reject(new Error('You are not a member of this conversation.'))

    connection.sendTyping('c1', true)
    await flush()

    expect(store().connection).toBe('connected')
  })
})

// ##### Shutting down #####

describe('dispose', () => {
  it('stops the connection and cancels pending typing timers', async () => {
    const { hub, connection } = await connected()
    hub.emit('UserTyping', 'c1', 'bob', 'Bob', true)

    connection.dispose()
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)
    await vi.advanceTimersByTimeAsync(TYPING_TTL_MS * 2)

    expect(hub.stops).toBe(1)
    // The cancelled timer did not fire, so the entry added by hand is still there.
    expect(store().typing.c1).toHaveLength(1)
  })
})

// ##### The session ending #####
// The server closes a connection when its token expires, and refuses a new one made with it. Trying
// again with the same token can never work, so the client ends the session instead of retrying for ever.

describe('an expired session', () => {
  it('ends the session instead of retrying when a first connect fails', async () => {
    const hub = new FakeHub()
    hub.startOutcomes = [new Error('401'), new Error('401'), undefined]

    const { onSessionExpired } = setup({ hub, sessionExpired: () => true })
    await flush()
    await vi.advanceTimersByTimeAsync(60_000)

    expect(onSessionExpired).toHaveBeenCalledTimes(1)
    expect(hub.startAttempts).toBe(1)
  })

  it('keeps retrying a failed first connect while the session is still valid', async () => {
    const hub = new FakeHub()
    hub.startOutcomes = [new Error('down'), undefined]

    const { onSessionExpired } = setup({ hub, sessionExpired: () => false })
    await flush()
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0])

    expect(hub.startAttempts).toBe(2)
    expect(onSessionExpired).not.toHaveBeenCalled()
  })

  it('ends the session when the connection starts to drop and the token has expired', async () => {
    let expired = false
    const { hub, onSessionExpired } = setup({ sessionExpired: () => expired })
    await flush()

    expired = true
    hub.fireReconnecting()

    expect(onSessionExpired).toHaveBeenCalledTimes(1)
  })

  it('ends the session when the library gives up and the token has expired', async () => {
    let expired = false
    const { hub, onSessionExpired } = setup({ sessionExpired: () => expired })
    await flush()

    expired = true
    hub.fireClosed()

    expect(onSessionExpired).toHaveBeenCalledTimes(1)
    expect(store().connection).toBe('disconnected')
  })

  it('reports it once even though a drop is followed by a close', async () => {
    const { hub, onSessionExpired } = setup({ sessionExpired: () => true })
    await flush()

    hub.fireReconnecting()
    hub.fireClosed()
    hub.fireClosed()

    expect(onSessionExpired).toHaveBeenCalledTimes(1)
  })

  it('does not end the session for a drop while the token is still valid', async () => {
    const { hub, onSessionExpired } = setup({ sessionExpired: () => false })
    await flush()

    hub.fireReconnecting()
    hub.fireClosed()

    expect(onSessionExpired).not.toHaveBeenCalled()
  })

  it('says nothing after being disposed, because disposing closes the connection too', async () => {
    const { connection, onSessionExpired } = setup({ sessionExpired: () => true })
    await flush()

    connection.dispose()
    await flush()

    expect(onSessionExpired).not.toHaveBeenCalled()
  })
})
