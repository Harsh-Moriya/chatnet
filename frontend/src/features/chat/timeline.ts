import { formatDayLabel } from '../../lib/format'
import type { Message, PendingMessage } from './types'

// Turns a conversation's messages into the list the screen draws: day headings, and for each
// message whether it continues a run from the same sender. Pure, so the rules can be tested.

// Messages from one sender this close together read as one run: the sender is named once and the
// spacing is tighter.
export const RUN_GAP_MS = 5 * 60 * 1000

export type TimelineItem =
  | { kind: 'day'; key: string; label: string }
  | {
      kind: 'message'
      key: string
      message: Message
      own: boolean
      // Show the sender's name above the message. Only in groups, only for other people, and only
      // at the start of a run: in a direct chat it is obvious who is talking.
      showSender: boolean
      continuing: boolean
    }
  | { kind: 'pending'; key: string; pending: PendingMessage; continuing: boolean }

export interface TimelineOptions {
  isGroup: boolean
  currentUserId: string
  now: Date
  locale?: string
}

interface Last {
  senderId: string
  time: number
  day: string
}

const dayKey = (date: Date) => `${date.getFullYear()}-${date.getMonth() + 1}-${date.getDate()}`

export function buildTimeline(
  messages: Message[],
  pending: PendingMessage[],
  { isGroup, currentUserId, now, locale }: TimelineOptions,
): TimelineItem[] {
  const items: TimelineItem[] = []
  let currentDay: string | null = null
  let last: Last | null = null

  // Adds a heading when the day differs from the previous item's, and returns the day.
  function enterDay(createdAt: string): string {
    const day = dayKey(new Date(createdAt))
    if (day !== currentDay) {
      items.push({ kind: 'day', key: `day:${day}`, label: formatDayLabel(createdAt, now, locale) })
      currentDay = day
    }
    return day
  }

  for (const message of messages) {
    const day = enterDay(message.createdAt)
    const time = new Date(message.createdAt).getTime()
    const own = message.senderId === currentUserId

    const continuing =
      last !== null && last.senderId === message.senderId && last.day === day && time - last.time <= RUN_GAP_MS

    items.push({
      kind: 'message',
      // The id this tab invented, when there is one, so that an unconfirmed message and its
      // confirmed copy are the same element to React and do not flash when swapped.
      key: message.clientMessageId ?? message.id,
      message,
      own,
      showSender: isGroup && !own && !continuing,
      continuing,
    })

    last = { senderId: message.senderId, time, day }
  }

  // Unconfirmed messages are the user's own and newer than anything confirmed, so they go last.
  // Their time is this browser's clock, which is not comparable with the server's, so a run is
  // judged only by who sent the previous message.
  for (const waiting of pending) {
    const day = enterDay(waiting.createdAt)
    const continuing = last !== null && last.senderId === currentUserId && last.day === day

    items.push({ kind: 'pending', key: waiting.clientMessageId, pending: waiting, continuing })

    last = { senderId: currentUserId, time: new Date(waiting.createdAt).getTime(), day }
  }

  return items
}
