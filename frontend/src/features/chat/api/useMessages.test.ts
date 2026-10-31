import { focusManager, InfiniteQueryObserver, QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { HISTORY_PAGE_SIZE } from '../history'
import { useChatStore } from '../store/chatStore'
import type { Message } from '../types'
import { messagesQueryOptions } from './useMessages'

// An infinite query that has loaded several pages refetches ALL of them, one after the other, when
// it goes stale and the window regains focus or the conversation is reopened. These tests use the
// real query library with the real options of the history query and count the requests.

function page(start: number): Message[] {
  return Array.from({ length: HISTORY_PAGE_SIZE }, (_, i) => ({
    id: `m${String(start + i).padStart(6, '0')}`,
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: 'x',
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
  }))
}

// The observer type is taken from the real options, so the test cannot drift from what the app uses.
const createObserver = (queryClient: QueryClient) =>
  new InfiniteQueryObserver(queryClient, messagesQueryOptions('c1'))

let client: QueryClient
let observer: ReturnType<typeof createObserver>
let requests: number

beforeEach(async () => {
  useChatStore.getState().reset()
  useChatStore.getState().setConnection('connected')

  requests = 0
  vi.spyOn(api, 'get').mockImplementation(async () => {
    requests++
    return { data: page(requests * HISTORY_PAGE_SIZE) } as never
  })

  // A query that goes stale at once, which is the worst case for an automatic refresh.
  client = new QueryClient({ defaultOptions: { queries: { staleTime: 0, retry: false } } })
  client.mount()
  observer = createObserver(client)
  observer.subscribe(() => {})

  await observer.refetch()
  for (let i = 0; i < 3; i++) await observer.fetchNextPage()
  requests = 0
})

afterEach(() => {
  client.clear()
  client.unmount()
  vi.restoreAllMocks()
})

const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 50))

describe('the history query', () => {
  it('has loaded the latest page and three older ones to begin with', () => {
    expect(observer.getCurrentResult().data?.pages).toHaveLength(4)
  })

  it('does not reload its pages when the window regains focus', async () => {
    focusManager.setFocused(false)
    focusManager.setFocused(true)
    await settle()

    expect(requests).toBe(0)
  })

  it('does not reload its pages when another observer mounts it again, as reopening a conversation does', async () => {
    const reopened = createObserver(client)
    const unsubscribe = reopened.subscribe(() => {})
    await settle()
    unsubscribe()

    expect(requests).toBe(0)
  })

  it('can still be asked to reload, which is what "Try again" does', async () => {
    await observer.refetch()

    expect(requests).toBe(4)
  })

  it('reloads from the latest page when it is reset, which is what a catch-up that fell too far behind does', async () => {
    await client.resetQueries({ queryKey: ['messages', 'c1'] })
    await settle()

    expect(requests).toBe(1)
    expect(observer.getCurrentResult().data?.pages).toHaveLength(1)
  })
})
