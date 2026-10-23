import { unreadLabel } from '../chat/readState'
import type { Conversation } from '../chat/types'

export const APP_TITLE = 'ChatNET'

// The unread messages across the conversations the user has not muted: what the number in the tab title adds up.
export function totalUnread(conversations: Record<string, Conversation>): number {
  return Object.values(conversations).reduce((sum, c) => (c.isMuted ? sum : sum + c.unreadCount), 0)
}

// "(3) ChatNET" while something is unread, so a background tab shows it, and plain "ChatNET" otherwise. A number
// at the cap reads "99+", as it does in the sidebar.
export function documentTitle(unread: number): string {
  return unread > 0 ? `(${unreadLabel(unread)}) ${APP_TITLE}` : APP_TITLE
}
