import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { HISTORY_PAGE_SIZE } from '../history'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Message } from '../types'
import { catchUpAll } from './catchUp'
import { loadNewerInWindow, loadOlderInWindow, openMessage, sendMessage } from './chatApi'
import { asServerSends, resetKeysDouble, wire } from './keysDouble'

// The keys of a conversation need a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('./keysDouble')).keysModule)

const store = () => useChatStore.getState()

const respond = <T>(data: T) => ({ data: asServerSends(data) }) as AxiosResponse<T>

// Messages numbered in time: "m5" is the fifth minute of the hour, so a page is easy to state.
function message(n: number, overrides: Partial<Message> = {}): Message {
  return {
    id: `m${String(n).padStart(4, '0')}`,
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: `text ${n}`,
    createdAt: new Date(Date.UTC(2026, 9, 11, 0, n, 0)).toISOString(),
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    ...overrides,
  }
}

const range = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => message(from + i))

function conversation(): Conversation {
  return {
    id: 'c1', type: 'Direct', name: 'Bob', avatarUrl: null, updatedAt: '2026-10-11T10:30:00Z', lastMessage: null,
    unreadCount: 0, otherUserId: 'bob', isMuted: false, leftAt: null, keyEpoch: 0, keyRotationRequired: false,
  }
}

const idsHeld = () => store().messages.c1.map((m) => m.id)

beforeEach(() => {
  store().reset()
  store().setConnection('connected')
  store().seedConversations([conversation()])
  resetKeysDouble()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('opening a message in its place', () => {
  it('asks for the history around it and shows it as a window, with more in both directions when the pages were full', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(range(100, 200)))

    await openMessage('c1', message(150).id)

    expect(get).toHaveBeenCalledWith('/conversations/c1/messages', {
      params: { before: undefined, after: undefined, around: message(150).id },
      signal: undefined,
    })
    expect(idsHeld()).toHaveLength(101)
    expect(store().windows.c1).toEqual({ buffer: [], hasOlder: true })
  })

  it('knows the start of the history was reached when the older page was short', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(respond(range(1, 60)))

    await openMessage('c1', message(10).id)

    expect(store().windows.c1.hasOlder).toBe(false)
    expect(store().windows.c1.buffer).toEqual([])
  })

  it('is live at once when the newer page was short, because the newest message is in it', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(respond(range(1, 120)))

    await openMessage('c1', message(100).id)

    expect(store().windows.c1.buffer).toBeNull()
  })

  it('takes exactly a full newer page as not yet the end, and one message less as the end', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(respond(range(51, 51 + HISTORY_PAGE_SIZE * 2)))
    await openMessage('c1', message(101).id)
    expect(store().windows.c1.buffer).toEqual([])

    store().reset()
    store().setConnection('connected')
    vi.spyOn(api, 'get').mockResolvedValue(respond(range(51, 50 + HISTORY_PAGE_SIZE * 2)))
    await openMessage('c1', message(101).id)
    expect(store().windows.c1.buffer).toBeNull()
  })

  it('leaves what is held alone when the server refuses', async () => {
    store().seedMessages('c1', [message(500)])
    vi.spyOn(api, 'get').mockRejectedValue(
      new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, { status: 400, data: {} } as AxiosResponse),
    )

    await expect(openMessage('c1', 'unknown')).rejects.toBeInstanceOf(AxiosError)

    expect(idsHeld()).toEqual([message(500).id])
    expect(store().windows.c1).toBeUndefined()
  })
})

describe('reading on from a window', () => {
  beforeEach(async () => {
    vi.spyOn(api, 'get').mockResolvedValueOnce(respond(range(100, 200)))
    await openMessage('c1', message(150).id)
    vi.restoreAllMocks()
  })

  it('asks for the page just older than the first message held and adds it above', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(range(50, 99)))

    await loadOlderInWindow('c1')

    expect(get).toHaveBeenCalledWith('/conversations/c1/messages', {
      params: { before: message(100).id, after: undefined, around: undefined },
      signal: undefined,
    })
    expect(idsHeld()[0]).toBe(message(50).id)
    expect(idsHeld()).toHaveLength(151)
    expect(store().windows.c1.hasOlder).toBe(true)
  })

  it('knows the start was reached when the page is short', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(respond(range(90, 99)))

    await loadOlderInWindow('c1')

    expect(store().windows.c1.hasOlder).toBe(false)
  })

  it('asks for the page just newer than the last message held, and stays behind the window while the page is full', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(range(201, 250)))

    await loadNewerInWindow('c1')

    expect(get).toHaveBeenCalledWith('/conversations/c1/messages', {
      params: { before: undefined, after: message(200).id, around: undefined },
      signal: undefined,
    })
    expect(idsHeld().at(-1)).toBe(message(250).id)
    expect(store().windows.c1.buffer).toEqual([])
  })

  it('goes live when the page is short, with what was pushed meanwhile joined to it exactly once', async () => {
    // The last two arrived by push after the server made the page; the third is in both.
    store().receiveMessage(message(242), 'me')
    store().receiveMessage(message(241), 'me')
    store().receiveMessage(message(240), 'me')
    vi.spyOn(api, 'get').mockResolvedValue(respond(range(201, 240)))

    await loadNewerInWindow('c1')

    expect(store().windows.c1.buffer).toBeNull()
    expect(idsHeld().slice(-3)).toEqual([message(240).id, message(241).id, message(242).id])
    expect(new Set(idsHeld()).size).toBe(idsHeld().length)
  })

  it('does nothing for a conversation with nothing held', async () => {
    const get = vi.spyOn(api, 'get')

    await loadOlderInWindow('empty')
    await loadNewerInWindow('empty')

    expect(get).not.toHaveBeenCalled()
  })
})

describe('sending from a window', () => {
  it('goes back to the newest messages first, so the message appears where the conversation is', async () => {
    store().showWindow('c1', range(100, 120), { hasOlder: true, reachesNewest: false })
    vi.spyOn(api, 'post').mockResolvedValue(respond(wire(message(999, { senderId: 'me' }))))

    await sendMessage('c1', 'hello')

    expect(store().windows.c1).toBeUndefined()
    expect(store().messages.c1.map((m) => m.id)).toEqual([message(999).id])
  })

  it('keeps what is held when the conversation is live, even if it was reached through a window', async () => {
    store().showWindow('c1', range(100, 120), { hasOlder: true, reachesNewest: true })
    vi.spyOn(api, 'post').mockResolvedValue(respond(wire(message(999, { senderId: 'me' }))))

    await sendMessage('c1', 'hello')

    expect(store().windows.c1).toEqual({ buffer: null, hasOlder: true })
    expect(idsHeld()).toHaveLength(22)
  })
})

describe('catching up after a reconnect', () => {
  it('leaves a conversation read from a window that stops short alone, and still catches up the others', async () => {
    store().seedConversations([{ ...conversation(), id: 'c2' }])
    store().showWindow('c1', range(100, 120), { hasOlder: true, reachesNewest: false })
    store().seedMessages('c2', [message(5, { conversationId: 'c2' })])
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))

    await catchUpAll({ onTooFarBehind: () => {} })

    const asked = get.mock.calls.map((call) => call[0])
    expect(asked.every((url) => url === '/conversations/c2/messages')).toBe(true)
    expect(asked.length).toBeGreaterThan(0)
  })
})
