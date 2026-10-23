import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useChatStore } from '../../chat/store/chatStore'
import { NOTIFICATION_PAGE_SIZE, useNotificationStore } from '../store/notificationStore'
import type { NotificationItem, NotificationList } from '../types'
import {
  dismissNotification,
  fetchNotifications,
  fetchOlderNotifications,
  markNotificationsRead,
} from './notificationsApi'

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
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0))

beforeEach(() => {
  store().reset()
  useChatStore.getState().reset()
  useChatStore.getState().setConnection('connected')
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('loading the list', () => {
  it('asks for the newest page and puts it in the store', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(list([n(2), n(1)], 7)))

    await fetchNotifications()

    expect(get).toHaveBeenCalledWith('/notifications', expect.objectContaining({}))
    expect(store().items.map((i) => i.id)).toEqual(['n002', 'n001'])
    expect(store().unreadCount).toBe(7)
  })

  it('waits for the live connection first, so a push is not lost in the gap', async () => {
    useChatStore.getState().setConnection('connecting')
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(list([])))

    const loading = fetchNotifications()
    await flush()
    expect(get).not.toHaveBeenCalled()

    useChatStore.getState().setConnection('connected')
    await loading
    expect(get).toHaveBeenCalledTimes(1)
  })

  it('asks for the page before the oldest one held', async () => {
    store().seedFirstPage(list([n(3), n(2)]))
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(list([n(1)])))

    await fetchOlderNotifications()

    expect(get).toHaveBeenCalledWith('/notifications', { params: { before: 'n002' } })
    expect(store().items.map((i) => i.id)).toEqual(['n003', 'n002', 'n001'])
    expect(store().hasMore).toBe(false)
  })

  it('says there may be more when the older page is full', async () => {
    store().seedFirstPage(list([n(90)]))
    vi.spyOn(api, 'get').mockResolvedValue(
      respond(list(Array.from({ length: NOTIFICATION_PAGE_SIZE }, (_, i) => n(60 - i)))),
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
