import type { ReadPoint, ReadPointChange, StoredMessage } from './types'

// The rules of what the store keeps, as plain functions so they can be tested without a browser.

// Whether a copy of a message should be kept over the one held. A copy with a higher change number is newer. With the same number the
// copy that could be opened replaces the one that could not, which is how a message sealed for want of a key is opened later; nothing
// else replaces a copy of the same number.
export function supersedes(incoming: StoredMessage, held: StoredMessage | undefined): boolean {
  if (held === undefined) return true
  if (incoming.changeSeq !== held.changeSeq) return incoming.changeSeq > held.changeSeq

  return held.state === 'sealed' && incoming.state !== 'sealed'
}

// The messages of one page with each message once, as the copy to keep: a page read from the history while changes are being made can
// name a message twice.
export function newestOfEach(messages: StoredMessage[]): StoredMessage[] {
  const kept = new Map<string, StoredMessage>()

  for (const message of messages) {
    const key = `${message.conversationId}/${message.id}`
    if (supersedes(message, kept.get(key))) kept.set(key, message)
  }

  return [...kept.values()]
}

const later = (a: number | null, b: number | null): number | null => (a === null ? b : b === null ? a : Math.max(a, b))

// The read point after a page: the cursor only moves forward, the starting number is the first one written, the oldest message follows
// the page, and a history that has been read stays read. Nothing a slower tab writes can undo what a faster one has recorded.
export function mergeReadPoint(
  held: ReadPoint | undefined,
  userId: string,
  conversationId: string,
  change: ReadPointChange,
): ReadPoint {
  return {
    userId,
    conversationId,
    cursor: later(held?.cursor ?? null, change.cursor ?? null),
    startSeq: held?.startSeq ?? change.startSeq ?? null,
    oldestId: change.oldestId ?? held?.oldestId ?? null,
    historyRead: (held?.historyRead ?? false) || (change.historyRead ?? false),
  }
}
