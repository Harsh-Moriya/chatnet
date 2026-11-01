import { beforeEach, describe, expect, it } from 'vitest'
import { NOTIFICATION_PAGE_SIZE, useNotificationStore } from './notificationStore'
import type { NotificationItem } from '../types'

const store = () => useNotificationStore.getState()

const START = Date.UTC(2026, 9, 11, 10, 0, 0)

// Notifications are made newest-first by their second: n(5) is newer than n(4).
function n(second: number, overrides: Partial<NotificationItem> = {}): NotificationItem {
  const id = `n${String(second).padStart(3, '0')}`
  return {
    id,
    type: 'Mention',
    conversationId: 'c1',
    actorId: 'bob',
    actorDisplayName: 'Bob',
    messageId: `m${second}`,
    messagePreview: 'hello',
    isRead: false,
    createdAt: new Date(START + second * 1000).toISOString(),
    ...overrides,
  }
}

const ids = () => store().items.map((i) => i.id)
const fullPage = (from: number) => Array.from({ length: NOTIFICATION_PAGE_SIZE }, (_, i) => n(from - i))

beforeEach(() => {
  store().reset()
})

describe('loading the first page', () => {
  it('shows the notifications newest first and takes the count from the server', () => {
    store().seedFirstPage({ items: [n(3), n(2), n(1)], unreadCount: 3 })

    expect(ids()).toEqual(['n003', 'n002', 'n001'])
    expect(store().unreadCount).toBe(3)
    expect(store().loaded).toBe(true)
  })

  it('uses the server count even when more are unread than the page holds', () => {
    store().seedFirstPage({ items: fullPage(40), unreadCount: 100 })

    expect(store().unreadCount).toBe(100)
  })

  it('never reports fewer unread than the notifications held', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 0 })

    expect(store().unreadCount).toBe(2)
  })

  it('says there may be more only when the page was full', () => {
    store().seedFirstPage({ items: fullPage(40), unreadCount: 0 })
    expect(store().hasMore).toBe(true)

    store().seedFirstPage({ items: [n(3)], unreadCount: 0 })
    expect(store().hasMore).toBe(false)
  })

  it('keeps a push that arrived after the page was computed', () => {
    store().receive(n(9))

    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 2 })

    expect(ids()).toEqual(['n009', 'n003', 'n002'])
    expect(store().unreadCount).toBe(3)
  })

  it('keeps everything pushed when the page is empty', () => {
    store().receive(n(9))

    store().seedFirstPage({ items: [], unreadCount: 0 })

    expect(ids()).toEqual(['n009'])
  })

  it('does not bring back a notification that was read after the page was requested', () => {
    store().seedFirstPage({ items: [n(2)], unreadCount: 1 })
    store().applyRead(['n002'])

    store().seedFirstPage({ items: [n(2)], unreadCount: 1 })

    expect(store().items[0].isRead).toBe(true)
    // The server's count still included it.
    expect(store().unreadCount).toBe(0)
  })

  it('does not bring back a notification that was dismissed after the page was requested', () => {
    store().seedFirstPage({ items: [n(2), n(1)], unreadCount: 2 })
    store().applyDismissed('n002')

    store().seedFirstPage({ items: [n(2), n(1)], unreadCount: 2 })

    expect(ids()).toEqual(['n001'])
    expect(store().unreadCount).toBe(1)
  })

  it('drops one that is gone from the page (dismissed elsewhere)', () => {
    store().seedFirstPage({ items: [n(3), n(2), n(1)], unreadCount: 3 })

    store().seedFirstPage({ items: [n(3), n(1)], unreadCount: 2 })

    expect(ids()).toEqual(['n003', 'n001'])
  })

  it('keeps older pages that were loaded while the new page reaches into them', () => {
    store().seedFirstPage({ items: fullPage(90), unreadCount: 0 })
    store().appendOlder(fullPage(60))

    // The same newest page again, plus one push: it overlaps what is held, so the older pages stay.
    store().seedFirstPage({ items: [n(91), ...fullPage(90).slice(0, NOTIFICATION_PAGE_SIZE - 1)], unreadCount: 0 })

    expect(store().items).toHaveLength(NOTIFICATION_PAGE_SIZE * 2 + 1)
    expect(store().hasMore).toBe(true)
  })

  it('does not offer more after the last page was loaded', () => {
    store().seedFirstPage({ items: fullPage(90), unreadCount: 0 })
    store().appendOlder([n(5)])

    store().seedFirstPage({ items: fullPage(90), unreadCount: 0 })

    expect(store().items).toHaveLength(NOTIFICATION_PAGE_SIZE + 1)
    expect(store().hasMore).toBe(false)
  })

  it('starts the list again from the page when it shares nothing with the older pages', () => {
    store().seedFirstPage({ items: fullPage(40), unreadCount: 0 })
    store().appendOlder(fullPage(10).map((item, i) => ({ ...item, id: `old${i}`, createdAt: n(-1 - i).createdAt })))

    // Many notifications arrived while nothing was listening: a full page that touches nothing held, so what lies
    // between it and the held ones is unknown.
    store().seedFirstPage({
      items: Array.from({ length: NOTIFICATION_PAGE_SIZE }, (_, i) => ({ ...n(1000 - i), id: `new${i}` })),
      unreadCount: 0,
    })

    expect(store().items).toHaveLength(NOTIFICATION_PAGE_SIZE)
    expect(store().items.every((i) => i.id.startsWith('new'))).toBe(true)
    expect(store().hasMore).toBe(true)
  })
})

describe('loading older pages', () => {
  it('adds them behind the ones held, in order', () => {
    store().seedFirstPage({ items: fullPage(90), unreadCount: 5 })

    store().appendOlder(fullPage(60))

    expect(store().items).toHaveLength(NOTIFICATION_PAGE_SIZE * 2)
    expect(store().items[0].id).toBe('n090')
    expect(store().items.at(-1)?.id).toBe('n031')
    expect(store().hasMore).toBe(true)
  })

  it('says there is nothing more when the page is short', () => {
    store().seedFirstPage({ items: fullPage(90), unreadCount: 0 })

    store().appendOlder([n(5)])

    expect(store().hasMore).toBe(false)
  })

  it('does not list one twice', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 0 })

    store().appendOlder([n(2), n(1)])

    expect(ids()).toEqual(['n003', 'n002', 'n001'])
  })

  it('does not bring back a dismissed one', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 0 })
    store().applyDismissed('n001')

    store().appendOlder([n(1)])

    expect(ids()).toEqual(['n003', 'n002'])
  })
})

describe('a pushed notification', () => {
  it('goes to the top and is one more unread', () => {
    store().seedFirstPage({ items: [n(3)], unreadCount: 1 })

    store().receive(n(4))

    expect(ids()).toEqual(['n004', 'n003'])
    expect(store().unreadCount).toBe(2)
  })

  it('is added to a count that is higher than the notifications held', () => {
    store().seedFirstPage({ items: fullPage(40), unreadCount: 100 })

    store().receive(n(99))

    expect(store().unreadCount).toBe(101)
  })

  it('is stored even before the first page has arrived, and counted', () => {
    store().receive(n(4))

    expect(ids()).toEqual(['n004'])
    expect(store().unreadCount).toBe(1)
    expect(store().loaded).toBe(false)
  })

  it('is counted once when the page and the push both bring it', () => {
    store().seedFirstPage({ items: [n(3)], unreadCount: 1 })

    store().receive(n(3))

    expect(ids()).toEqual(['n003'])
    expect(store().unreadCount).toBe(1)
  })

  it('is not counted when it arrives already read', () => {
    store().receive(n(4, { isRead: true }))

    expect(store().unreadCount).toBe(0)
  })

  it('does not come back after it was dismissed', () => {
    store().seedFirstPage({ items: [n(3)], unreadCount: 1 })
    store().applyDismissed('n003')

    store().receive(n(3))

    expect(ids()).toEqual([])
    expect(store().unreadCount).toBe(0)
  })

  it('does not make a read one unread again', () => {
    store().seedFirstPage({ items: [n(3)], unreadCount: 1 })
    store().applyRead(['n003'])

    store().receive(n(3))

    expect(store().items[0].isRead).toBe(true)
    expect(store().unreadCount).toBe(0)
  })
})

describe('reading', () => {
  it('marks the named ones and lowers the count by that many', () => {
    store().seedFirstPage({ items: [n(3), n(2), n(1)], unreadCount: 3 })

    store().applyRead(['n003', 'n001'])

    expect(store().items.map((i) => i.isRead)).toEqual([true, false, true])
    expect(store().unreadCount).toBe(1)
  })

  it('counts a repeated report once', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 2 })

    store().applyRead(['n003'])
    store().applyRead(['n003'])

    expect(store().unreadCount).toBe(1)
  })

  it('leaves the count alone for ones that are not held', () => {
    store().seedFirstPage({ items: fullPage(40), unreadCount: 100 })

    store().applyRead(['somewhere-further-back'])

    expect(store().unreadCount).toBe(100)
  })

  it('never takes the count below the unread ones held', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 2 })

    // The server count can be behind a push that is already held.
    useNotificationStore.setState({ unreadCount: 0 })
    store().applyRead(['n003'])

    expect(store().unreadCount).toBe(1)
  })

  it('lowers a count that is higher than the notifications held', () => {
    store().seedFirstPage({ items: fullPage(40), unreadCount: 100 })

    store().applyRead(['n040'])

    expect(store().unreadCount).toBe(99)
  })

  it('marks everything held and zeroes the count when all are read', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 57 })

    store().applyAllRead()

    expect(store().items.every((i) => i.isRead)).toBe(true)
    expect(store().unreadCount).toBe(0)
  })

  it('changes nothing when everything was already read', () => {
    store().seedFirstPage({ items: [n(3, { isRead: true })], unreadCount: 0 })
    const before = store()

    store().applyAllRead()

    expect(store()).toBe(before)
  })
})

describe('dismissing', () => {
  it('removes it, and lowers the count when it was unread', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 2 })

    store().applyDismissed('n003')

    expect(ids()).toEqual(['n002'])
    expect(store().unreadCount).toBe(1)
  })

  it('leaves the count alone when it was already read', () => {
    store().seedFirstPage({ items: [n(3, { isRead: true }), n(2)], unreadCount: 1 })

    store().applyDismissed('n003')

    expect(store().unreadCount).toBe(1)
  })

  it('counts a repeated report once', () => {
    store().seedFirstPage({ items: [n(3), n(2)], unreadCount: 2 })

    store().applyDismissed('n003')
    store().applyDismissed('n003')

    expect(store().unreadCount).toBe(1)
  })

  it('is remembered for one that is not held', () => {
    store().applyDismissed('n009')

    store().receive(n(9))

    expect(ids()).toEqual([])
  })
})

describe('resetting', () => {
  it('forgets everything, including what was dismissed', () => {
    store().seedFirstPage({ items: [n(3)], unreadCount: 1 })
    store().applyDismissed('n003')

    store().reset()
    store().receive(n(3))

    expect(ids()).toEqual(['n003'])
    expect(store().loaded).toBe(false)
  })
})

describe('a message that a notification is about changes', () => {
  it('shows the edited text in the notification', () => {
    store().seedFirstPage({ items: [n(2, { messagePreview: 'the old words' })], unreadCount: 1 })

    store().applyMessageChange('m2', 'the new words')

    expect(store().items[0].messagePreview).toBe('the new words')
  })

  it('shows only as much of it as the server would', () => {
    store().seedFirstPage({ items: [n(2)], unreadCount: 1 })

    store().applyMessageChange('m2', 'x'.repeat(250))

    expect(store().items[0].messagePreview).toHaveLength(100)
  })

  it('takes the text of a deleted message out of the notification', () => {
    store().seedFirstPage({ items: [n(2, { messagePreview: 'a secret' })], unreadCount: 1 })

    store().applyMessageChange('m2', null)

    expect(store().items[0].messagePreview).toBeNull()
  })

  it('does not bring the text of a deleted message back with a late edit', () => {
    store().seedFirstPage({ items: [n(2, { messagePreview: null })], unreadCount: 1 })

    store().applyMessageChange('m2', 'late edit')

    expect(store().items[0].messagePreview).toBeNull()
  })

  it('changes every notification about the message, and no other, and keeps read state and count', () => {
    store().seedFirstPage({
      items: [n(3, { messageId: 'm2', isRead: true }), n(2, { messageId: 'm2' }), n(1)],
      unreadCount: 2,
    })

    store().applyMessageChange('m2', null)

    expect(store().items.map((i) => i.messagePreview)).toEqual([null, null, 'hello'])
    expect(store().items.map((i) => i.isRead)).toEqual([true, false, false])
    expect(store().unreadCount).toBe(2)
  })

  it('returns the same state when nothing would change', () => {
    store().seedFirstPage({ items: [n(2, { messagePreview: 'same' })], unreadCount: 1 })
    const before = store().items

    store().applyMessageChange('m2', 'same')
    store().applyMessageChange('unknown', 'anything')

    expect(store().items).toBe(before)
  })
})
