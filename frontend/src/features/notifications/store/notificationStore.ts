import { create } from 'zustand'
import { compareTimestamps } from '../../../lib/time'
import type { NotificationItem, NotificationList } from '../types'

// The signed-in user's notifications, newest first. As with the chat store, two sources feed it and neither
// replaces the other: REST loads the pages the bell lists, and SignalR pushes what happens while it is open.
// Both go through the actions below, which merge instead of overwrite.

// How many the server returns in a page. A page of this size may be followed by another.
export const NOTIFICATION_PAGE_SIZE = 30

interface NotificationState {
  items: NotificationItem[]
  // How many are unread. The server counts to a cap, so at the cap this is "at least". Between loads it is
  // kept up to date by the pushes, and never drops below the unread notifications actually held.
  unreadCount: number
  // Whether the first page has arrived, so the panel can tell "still loading" from "nothing to show".
  loaded: boolean
  // Whether an older page may exist.
  hasMore: boolean
  // Dismissed in this session. A page that was already on its way when the dismissal happened still lists
  // them, and must not bring them back.
  dismissed: ReadonlySet<string>

  // The newest page, as the server has it now. It replaces what was held in its range. Kept besides it: what is
  // newer than the page (a push can arrive while the request is in flight), and the older pages already loaded,
  // but only while they join the new page. A page that shares no notification with them leaves a gap that
  // "load more" would never fill, so they go and the list starts again from the page.
  seedFirstPage: (list: NotificationList) => void
  // A page older than the ones held.
  appendOlder: (items: NotificationItem[]) => void
  // A push: someone mentioned the user, or added them to a group.
  receive: (item: NotificationItem) => void
  applyRead: (ids: readonly string[]) => void
  applyAllRead: () => void
  applyDismissed: (id: string) => void
  reset: () => void
}

type Data = Pick<NotificationState, 'items' | 'unreadCount' | 'loaded' | 'hasMore' | 'dismissed'>

const emptyData = (): Data => ({
  items: [],
  unreadCount: 0,
  loaded: false,
  hasMore: false,
  dismissed: new Set(),
})

// Newest first, the order the server lists them in; the id settles ties.
function newestFirst(a: NotificationItem, b: NotificationItem): number {
  return compareTimestamps(b.createdAt, a.createdAt) || (a.id < b.id ? 1 : a.id > b.id ? -1 : 0)
}

const unreadIn = (items: readonly NotificationItem[]) => items.filter((n) => !n.isRead).length

// Reading is one way: nothing makes a notification unread again. Of two copies of the same notification, the
// read one wins, so a page that was requested before a read and arrives after it cannot undo it.
function merged(held: NotificationItem, incoming: NotificationItem): NotificationItem {
  return held.isRead && !incoming.isRead ? { ...incoming, isRead: true } : incoming
}

export const useNotificationStore = create<NotificationState>()((set) => ({
  ...emptyData(),

  seedFirstPage: (list) =>
    set((state) => {
      const page = list.items.filter((n) => !state.dismissed.has(n.id))
      const byId = new Map(state.items.map((n) => [n.id, n]))
      const inPage = new Set(list.items.map((n) => n.id))
      const newest = list.items[0]
      const oldest = list.items.at(-1)
      const full = list.items.length >= NOTIFICATION_PAGE_SIZE

      // Held notifications newer than everything in the page arrived by push after the server answered. When
      // the page is empty everything held is such a push.
      const newer = state.items.filter((n) => !inPage.has(n.id) && (!newest || newestFirst(n, newest) < 0))

      // Older pages stay when this page reaches into them. A page that is not full has no older notifications
      // behind it at all.
      const joined = full && list.items.some((n) => byId.has(n.id))
      const older = joined && oldest ? state.items.filter((n) => !inPage.has(n.id) && newestFirst(n, oldest) > 0) : []

      const items = [...newer, ...page.map((n) => (byId.has(n.id) ? merged(byId.get(n.id)!, n) : n)), ...older].sort(
        newestFirst,
      )

      // The server counted before any read or dismissal this tab has seen since it was asked.
      const stale = list.items.filter((p) => !p.isRead && (byId.get(p.id)?.isRead || state.dismissed.has(p.id))).length

      return {
        items,
        unreadCount: Math.max(list.unreadCount - stale, unreadIn(items)),
        loaded: true,
        hasMore: older.length > 0 ? state.hasMore : full,
      }
    }),

  appendOlder: (older) =>
    set((state) => {
      const byId = new Map(state.items.map((n) => [n.id, n]))
      for (const n of older) {
        if (!state.dismissed.has(n.id)) byId.set(n.id, byId.has(n.id) ? merged(byId.get(n.id)!, n) : n)
      }

      const items = [...byId.values()].sort(newestFirst)
      return {
        items,
        unreadCount: Math.max(state.unreadCount, unreadIn(items)),
        hasMore: older.length >= NOTIFICATION_PAGE_SIZE,
      }
    }),

  // The same notification can arrive twice (a push and a page), so a known one is only merged. A new unread one
  // is one more unread.
  receive: (item) =>
    set((state) => {
      if (state.dismissed.has(item.id)) return state

      const held = state.items.find((n) => n.id === item.id)
      if (held) {
        const next = merged(held, item)
        return next === held ? state : { items: state.items.map((n) => (n.id === item.id ? next : n)) }
      }

      const items = [...state.items, item].sort(newestFirst)
      return { items, unreadCount: Math.max(state.unreadCount + (item.isRead ? 0 : 1), unreadIn(items)) }
    }),

  // Only the ones held and unread change the count: those are the transitions that are known. A report for one
  // that is not held (older than the pages loaded) leaves the count as it is until the next load.
  applyRead: (ids) =>
    set((state) => {
      const wanted = new Set(ids)
      const turning = state.items.filter((n) => wanted.has(n.id) && !n.isRead)
      if (turning.length === 0) return state

      const items = state.items.map((n) => (wanted.has(n.id) ? { ...n, isRead: true } : n))
      return { items, unreadCount: Math.max(state.unreadCount - turning.length, unreadIn(items)) }
    }),

  applyAllRead: () =>
    set((state) =>
      state.unreadCount === 0 && unreadIn(state.items) === 0
        ? state
        : { items: state.items.map((n) => (n.isRead ? n : { ...n, isRead: true })), unreadCount: 0 },
    ),

  applyDismissed: (id) =>
    set((state) => {
      if (state.dismissed.has(id)) return state

      const dismissed = new Set(state.dismissed).add(id)
      const held = state.items.find((n) => n.id === id)
      if (!held) return { dismissed }

      const items = state.items.filter((n) => n.id !== id)
      const unreadCount = held.isRead ? state.unreadCount : state.unreadCount - 1
      return { dismissed, items, unreadCount: Math.max(unreadCount, unreadIn(items)) }
    }),

  reset: () => set(emptyData()),
}))
