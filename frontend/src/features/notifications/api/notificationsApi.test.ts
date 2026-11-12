import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useChatStore } from '../../chat/store/chatStore'
import { NOTIFICATION_PAGE_SIZE, useNotificationStore } from '../store/notificationStore'
import { opener, resetKeysDouble, sealed } from '../../chat/api/keysDouble'
import type { NotificationItem, NotificationList, NotificationListWire } from '../types'
import {
  dismissNotification,
  fetchNotifications,
  fetchOlderNotifications,
  markNotificationsRead,
} from './notificationsApi'

// Opening what the server sends needs a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('../../chat/api/keysDouble')).keysModule)

const store = () => useNotificationStore.getState()

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

function failure(status: number, detail: string): AxiosError {
  return new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
    status,
    data: { detail },
  } as AxiosResponse)
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => {
    resolve = res
  })
  return { promise, resolve }
}

const START = Date.UTC(2026, 9, 11, 10, 0, 0)

function n(second: number, overrides: Partial<NotificationItem> = {}): NotificationItem {
  return {
    id: `n${String(second).padStart(3, '0')}`,
    type: 'Mention',
    conversationId: 'c1',
    actorId: 'bob',
    actorDisplayName: 'Bob',
    messageId: 'm1',
    messagePreview: 'hello',
    isRead: false,
    createdAt: new Date(START + second * 1000).toISOString(),
    ...overrides,
  }
}

const list = (items: NotificationItem[], unreadCount = items.length): NotificationList => ({ items, unreadCount })

// The list as the server sends it: the message a mention is about sealed.
const wireList = (items: NotificationItem[], unreadCount = items.length): NotificationListWire => ({
  unreadCount,
  items: items.map(({ messagePreview, ...rest }) => ({
    ...rest,
    message:
      messagePreview === null ? null : { ciphertext: sealed(messagePreview), keyEpoch: 1, clientMessageId: 'cid-1', editCount: 0 },
  })),
})
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0))

beforeEach(() => {
  store().reset()
  useChatStore.getState().reset()
  useChatStore.getState().setConnection('connected')
  resetKeysDouble()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('loading the list', () => {
  it('asks for the newest page and puts it in the store', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(wireList([n(2), n(1)], 7)))

    await fetchNotifications()

    expect(get).toHaveBeenCalledWith('/notifications', expect.objectContaining({}))
    expect(store().items.map((i) => i.id)).toEqual(['n002', 'n001'])
    expect(store().unreadCount).toBe(7)
  })

  it('shows the start of the message of each mention, opened here', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(
      respond(wireList([n(2, { messagePreview: 'first words' }), n(1, { type: 'MemberAdded', messageId: null, messagePreview: null })])),
    )

    const loaded = await fetchNotifications()

    expect(store().items.map((i) => i.messagePreview)).toEqual(['first words', null])
    expect(loaded.items.map((i) => i.messagePreview)).toEqual(['first words', null])
    expect(store().items[0]).not.toHaveProperty('message')
  })

  it('shows a mention whose message cannot be opened, without a preview', async () => {
    opener.open.mockResolvedValueOnce({ readable: false, reason: 'noKey' } as never)
    vi.spyOn(api, 'get').mockResolvedValue(respond(wireList([n(2), n(1)])))

    await fetchNotifications()

    expect(store().items.map((i) => i.messagePreview)).toEqual([null, 'hello'])
  })

  it('opens the older page too', async () => {
    store().seedFirstPage(list([n(3)]))
    vi.spyOn(api, 'get').mockResolvedValue(respond(wireList([n(2, { messagePreview: 'older words' })])))

    await fetchOlderNotifications()

    expect(store().items.map((i) => i.messagePreview)).toEqual(['hello', 'older words'])
  })

  it('waits for the live connection first, so a push is not lost in the gap', async () => {
    useChatStore.getState().setConnection('connecting')
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(wireList([])))

    const loading = fetchNotifications()
    await flush()
    expect(get).not.toHaveBeenCalled()

    useChatStore.getState().setConnection('connected')
    await loading
    expect(get).toHaveBeenCalledTimes(1)
  })

  it('asks for the page before the oldest one held', async () => {
    store().seedFirstPage(list([n(3), n(2)]))
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(wireList([n(1)])))

    await fetchOlderNotifications()

    expect(get).toHaveBeenCalledWith('/notifications', { params: { before: 'n002' } })
    expect(store().items.map((i) => i.id)).toEqual(['n003', 'n002', 'n001'])
    expect(store().hasMore).toBe(false)
  })

  it('says there may be more when the older page is full', async () => {
    store().seedFirstPage(list([n(90)]))
    vi.spyOn(api, 'get').mockResolvedValue(
      respond(wireList(Array.from({ length: NOTIFICATION_PAGE_SIZE }, (_, i) => n(60 - i)))),
    )

    await fetchOlderNotifications()

    expect(store().hasMore).toBe(true)
  })

  it('asks for nothing when nothing is held', async () => {
    const get = vi.spyOn(api, 'get')

    await fetchOlderNotifications()

    expect(get).not.toHaveBeenCalled()
  })

  it('leaves the list as it was when the request fails, and lets the caller show why', async () => {
    store().seedFirstPage(list([n(3)]))
    vi.spyOn(api, 'get').mockRejectedValue(failure(500, 'boom'))

    await expect(fetchOlderNotifications()).rejects.toThrow()

    expect(store().items.map((i) => i.id)).toEqual(['n003'])
  })
})

describe('marking read', () => {
  it('names the ones to mark, and applies it once the server has accepted', async () => {
    store().seedFirstPage(list([n(3), n(2)]))
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(undefined))

    await markNotificationsRead(['n003'])

    expect(post).toHaveBeenCalledWith('/notifications/read', { ids: ['n003'] })
    expect(store().items.map((i) => i.isRead)).toEqual([true, false])
    expect(store().unreadCount).toBe(1)
  })

  it('marks everything when none are named', async () => {
    store().seedFirstPage(list([n(3), n(2)], 40))
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(undefined))

    await markNotificationsRead()

    expect(post).toHaveBeenCalledWith('/notifications/read', { ids: undefined })
    expect(store().items.every((i) => i.isRead)).toBe(true)
    expect(store().unreadCount).toBe(0)
  })

  it('does not apply it before the server has answered', async () => {
    store().seedFirstPage(list([n(3)]))
    const response = deferred<AxiosResponse>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)

    const marking = markNotificationsRead(['n003'])
    await flush()
    expect(store().items[0].isRead).toBe(false)

    response.resolve(respond(undefined))
    await marking
    expect(store().items[0].isRead).toBe(true)
  })

  it('leaves everything unread when the server refuses', async () => {
    store().seedFirstPage(list([n(3)]))
    vi.spyOn(api, 'post').mockRejectedValue(failure(400, 'nope'))

    await expect(markNotificationsRead(['n003'])).rejects.toThrow()

    expect(store().items[0].isRead).toBe(false)
    expect(store().unreadCount).toBe(1)
  })
})

describe('dismissing', () => {
  it('deletes it on the server, then removes it here', async () => {
    store().seedFirstPage(list([n(3), n(2)]))
    const del = vi.spyOn(api, 'delete').mockResolvedValue(respond(undefined))

    await dismissNotification('n003')

    expect(del).toHaveBeenCalledWith('/notifications/n003')
    expect(store().items.map((i) => i.id)).toEqual(['n002'])
    expect(store().unreadCount).toBe(1)
  })

  it('keeps it when the server refuses', async () => {
    store().seedFirstPage(list([n(3)]))
    vi.spyOn(api, 'delete').mockRejectedValue(failure(404, 'gone'))

    await expect(dismissNotification('n003')).rejects.toThrow()

    expect(store().items.map((i) => i.id)).toEqual(['n003'])
  })
})
