import { formatLastSeen } from '../../lib/format'
import type { Presence } from './types'

// What to say about someone's presence, as plain functions so it can be tested without a browser.

// The line under the name in a direct chat, or null while nothing is known (the usual line stays).
export function directStatus(presence: Presence | undefined, now: Date, locale?: string): string | null {
  if (!presence) return null
  if (presence.isOnline) return 'Online'
  if (presence.lastSeenAt) return `Last seen ${formatLastSeen(presence.lastSeenAt, now, locale)}`
  return 'Offline'
}

// The line under the name of a group, or null when nobody is online (the usual line stays).
export function groupStatus(online: number): string | null {
  return online > 0 ? `${online} online` : null
}

// How many of these people are online. Someone nothing is known about is not counted.
export function countOnline(userIds: string[], byUser: Record<string, Presence>): number {
  return userIds.filter((id) => byUser[id]?.isOnline === true).length
}
