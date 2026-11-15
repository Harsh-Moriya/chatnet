import type { AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useChatStore } from '../store/chatStore'
import type { Message } from '../types'
import { requestMessageFiles } from './chatApi'
import { asServerSends, resetKeysDouble } from './keysDouble'

// The keys of a conversation need a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('./keysDouble')).keysModule)

const store = () => useChatStore.getState()

const respond = <T>(data: T) => ({ data: asServerSends(data) }) as AxiosResponse<T>

const message = (n: number): Message => ({
  id: `m${n}`,
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
})

beforeEach(() => {
  store().reset()
  store().setConnection('connected')
  resetKeysDouble()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('the messages of a conversation that have files', () => {
  it('asks for the newest page when it has no place to continue from, and for the page just older when it has', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))

    await requestMessageFiles('c1')
    await requestMessageFiles('c1', { before: 'm9' })

    expect(get).toHaveBeenNthCalledWith(1, '/conversations/c1/attachments', { params: { before: undefined }, signal: undefined })
    expect(get).toHaveBeenNthCalledWith(2, '/conversations/c1/attachments', { params: { before: 'm9' }, signal: undefined })
  })

  it('opens the page in the order the server sent it, newest first', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(respond([message(3), message(2), message(1)]))

    const page = await requestMessageFiles('c1')

    expect(page.map((m) => m.id)).toEqual(['m3', 'm2', 'm1'])
    expect(page.map((m) => m.content)).toEqual(['text 3', 'text 2', 'text 1'])
  })

  it('does not add what it reads to the conversation the chat holds', async () => {
    store().seedMessages('c1', [message(50)])
    vi.spyOn(api, 'get').mockResolvedValue(respond([message(3), message(2)]))

    await requestMessageFiles('c1')

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m50'])
  })
})
