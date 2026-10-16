import { compareTimestamps } from '../../lib/time'
import type { Member, Message } from './types'

// The rules for read state, as plain functions so they can be tested without a browser. The store
// holds the numbers; these decide what to show and when to tell the server.

// The server counts unread messages up to this many and stops. Matches ConversationRepository.UnreadCap.
export const UNREAD_CAP = 100

// The text of the badge next to a conversation. At the cap the real number is unknown, so it reads
// "99+" rather than a number that might be wrong.
export function unreadLabel(count: number): string {
  if (count <= 0) return ''
  return count >= UNREAD_CAP ? `${UNREAD_CAP - 1}+` : String(count)
}

interface MarkReadSituation {
  // The page is in front of the reader. A hidden tab shows nothing to anyone.
  visible: boolean
  // The reader is looking at the end of the conversation, so the newest messages are on screen.
  atBottom: boolean
  unreadCount: number
  // Confirmed messages of the conversation, oldest first.
  messages: Message[] | undefined
}

// The message to tell the server "I have read up to here" about, or null when there is nothing to
// say yet. Only an unread conversation needs saying, and only while its newest messages are
// actually in front of the reader.
export function messageToMarkRead({ visible, atBottom, unreadCount, messages }: MarkReadSituation): string | null {
  if (!visible || !atBottom || unreadCount <= 0 || !messages || messages.length === 0) return null
  return messages[messages.length - 1].id
}

// The other members who have read a message: their read position is at or after it.
export function readersOf(message: Message, members: Member[] | undefined, currentUserId: string): Member[] {
  if (!members) return []

  return members.filter(
    (m) =>
      m.userId !== currentUserId &&
      m.lastReadAt !== null &&
      compareTimestamps(m.lastReadAt, message.createdAt) >= 0,
  )
}

export interface Seen {
  messageId: string
  label: string
  title: string
}

// The "seen" mark shown under the reader's own newest message, or null when nobody has read it. It
// belongs to the newest message only: a mark under an older one would be easy to take for the
// message the reader just sent.
export function seenFor(
  messages: Message[],
  members: Member[] | undefined,
  currentUserId: string,
  isGroup: boolean,
): Seen | null {
  const newestOwn = [...messages].reverse().find((m) => m.senderId === currentUserId)
  if (!newestOwn) return null

  const readers = readersOf(newestOwn, members, currentUserId)
  if (readers.length === 0) return null

  return {
    messageId: newestOwn.id,
    label: isGroup ? `Seen by ${readers.length}` : 'Seen',
    title: `Seen by ${readers.map((r) => r.displayName).join(', ')}`,
  }
}
