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
  return { id, type: 'Direct', name: 'Bob', avatarUrl: null, updatedAt: '2026-10-11T09:00:00Z', lastMessage: null, unreadCount: 0, otherUserId: null }
}

const RETRY_DELAYS = [100, 200, 400]

function setup(options: { hub?: FakeHub } = {}) {
  const hub = options.hub ?? new FakeHub()
  const onReconnected = vi.fn()
  const onPresenceChanged = vi.fn()
  const onUnauthorized = vi.fn()
  const connection = connectChat(hub, {
    getUserId: () => 'me',
    onReconnected,
    onPresenceChanged,
    onUnauthorized,
    retryDelay: (attempt) => RETRY_DELAYS[Math.min(attempt, RETRY_DELAYS.length - 1)],
  })
  return { hub, connection, onReconnected, onPresenceChanged, onUnauthorized }
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
    for (const event of ['ReceiveMessage', 'AddedToConversation', 'UserTyping', 'MessageRead', 'PresenceChanged']) {
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
    expect(hub.emit('PresenceChanged', { userId: 'bob', isOnline: true, lastSeenAt: null, at: '2026-10-11T10:00:00Z' })).toBeUndefined()
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

  it('hands a presence change to whoever owns the connection, unchanged', async () => {
    const { hub, onPresenceChanged } = await connected()
    const change = { userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T10:00:00Z', at: '2026-10-11T10:00:00Z' }

    hub.emit('PresenceChanged', change)

    expect(onPresenceChanged).toHaveBeenCalledTimes(1)
    expect(onPresenceChanged).toHaveBeenCalledWith(change)
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

  it('shows disconnected the moment the connection closes', async () => {
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

// ##### The server closing the connection #####
// The server closes a connection when its access token runs out. The library does not reconnect after
// that kind of close, so the client starts again by itself. The access token factory renews the session
// as part of starting, so nothing here has to know about tokens.

describe('a connection the server closed', () => {
  it('starts again by itself and is connected once the round trip answers', async () => {
    const { hub } = await connected()

    hub.fireClosed()
    expect(store().connection).toBe('disconnected')
    await flush()

    expect(hub.startAttempts).toBe(2)
    expect(store().connection).toBe('connected')
  })

  it('tells the owner to reload, because what the server pushed in the gap was missed', async () => {
    const { hub, onReconnected } = await connected()
    expect(onReconnected).not.toHaveBeenCalled()

    hub.fireClosed()
    await flush()

    expect(onReconnected).toHaveBeenCalledTimes(1)
  })

  it('keeps trying with growing delays while the server cannot be reached', async () => {
    const { hub, onReconnected } = await connected()
    hub.startOutcomes = [new Error('down'), new Error('down')]

    hub.fireClosed()
    await flush()
    expect(hub.startAttempts).toBe(2)
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0])
    expect(hub.startAttempts).toBe(3)
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[1])

    expect(hub.startAttempts).toBe(4)
    expect(store().connection).toBe('connected')
    expect(onReconnected).toHaveBeenCalledTimes(1)
  })

  it('starts only one new connection when several closes arrive together', async () => {
    const { hub } = await connected()

    hub.fireClosed()
    hub.fireClosed()
    await flush()

    expect(hub.startAttempts).toBe(2)
  })

  // The check that the connection is usable can take a moment, and the server may close it again in that
  // time. That close must not be swallowed by the attempt that was still finishing.
  it('starts again when the connection closes while it was still being checked', async () => {
    const ping = deferred()
    const hub = new FakeHub()
    hub.invokeImpl = () => ping.promise
    setup({ hub })
    await flush()
    expect(hub.startAttempts).toBe(1)

    hub.fireClosed()
    await flush()

    expect(hub.startAttempts).toBe(2)
  })

  it('does not start again after being disposed, although disposing closes the connection', async () => {
    const { hub, connection } = await connected()

    connection.dispose()
    await flush()

    expect(hub.startAttempts).toBe(1)
  })

  it('stops trying once disposed in the middle of a restart', async () => {
    const { hub, connection } = await connected()
    hub.startOutcomes = [new Error('down'), new Error('down'), new Error('down')]
    hub.fireClosed()
    await flush()

    connection.dispose()
    await vi.advanceTimersByTimeAsync(60_000)

    expect(hub.startAttempts).toBe(2)
  })
})

// ##### A token the server refuses #####
// A 401 on a connection attempt means the server does not take the token the client holds (a clock that is
// behind, a replaced signing key). The library would retry with the same token for ever, so the owner is
// told to renew the session.

describe('a connection attempt the server refuses with 401', () => {
  // The shape the real library gives: a plain Error whose message ends in the status, with no statusCode.
  const unauthorized = () =>
    new Error("Failed to complete negotiation with the server: Error: {\"status\":401}: Status code '401'")

  it('asks for the session to be renewed, and connects with the next attempt', async () => {
    const hub = new FakeHub()
    hub.startOutcomes = [unauthorized(), undefined]
    const { onUnauthorized } = setup({ hub })

    await flush()
    expect(onUnauthorized).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0])

    expect(hub.startAttempts).toBe(2)
    expect(store().connection).toBe('connected')
  })

  it('asks every time a refusal repeats, so a renewal that did not take is tried again', async () => {
    const hub = new FakeHub()
    hub.startOutcomes = [unauthorized(), unauthorized(), unauthorized(), undefined]
    const { onUnauthorized } = setup({ hub })

    await flush()
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0] + RETRY_DELAYS[1] + RETRY_DELAYS[2])

    expect(onUnauthorized).toHaveBeenCalledTimes(3)
  })

  it('does not ask for any other failure, such as the server being down', async () => {
    const hub = new FakeHub()
    hub.startOutcomes = [new Error('down'), new Error("Failed to complete negotiation with the server: Error: {\"status\":503}: Status code '503'"), undefined]
    const { onUnauthorized } = setup({ hub })

    await flush()
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0] + RETRY_DELAYS[1])

    expect(onUnauthorized).not.toHaveBeenCalled()
    expect(store().connection).toBe('connected')
  })

  it('asks when the restart after a close is refused too', async () => {
    const { hub, onUnauthorized } = await connected()
    hub.startOutcomes = [unauthorized(), undefined]

    hub.fireClosed()
    await flush()

    expect(onUnauthorized).toHaveBeenCalledTimes(1)
  })

  it('says nothing when it was disposed while the attempt was failing', async () => {
    const hub = new FakeHub()
    hub.startGate = deferred()
    hub.startOutcomes = [unauthorized()]
    const { connection, onUnauthorized } = setup({ hub })
    await flush()

    connection.dispose()
    hub.startGate.resolve()
    await flush()

    expect(onUnauthorized).not.toHaveBeenCalled()
  })
})
