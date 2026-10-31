import { describe, expect, it } from 'vitest'
import { HISTORY_PAGE_SIZE, olderCursor, scrollTopAfterPrepend, wasPrepended } from './history'
import type { Message } from './types'

function page(size: number, firstId = 'first'): Message[] {
  return Array.from({ length: size }, (_, i) => ({
    id: i === 0 ? firstId : `m${i}`,
    conversationId: 'c1',
    senderId: 'u1',
    senderDisplayName: 'U',
    content: 'x',
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
  }))
}

describe('the cursor for older messages', () => {
  it('is the id of the oldest message of a full page', () => {
    expect(olderCursor(page(HISTORY_PAGE_SIZE, 'oldest'))).toBe('oldest')
  })

  it('does not exist once a page comes back short, because that was the start of the history', () => {
    expect(olderCursor(page(HISTORY_PAGE_SIZE - 1))).toBeUndefined()
  })

  it('does not exist for an empty page', () => {
    expect(olderCursor([])).toBeUndefined()
  })
})

describe('recognising older messages added above the reader', () => {
  it('is true when the first message changed', () => {
    expect(wasPrepended('b', 'a')).toBe(true)
  })

  it('is false when the first message is the same, as when a new message arrives at the bottom', () => {
    expect(wasPrepended('a', 'a')).toBe(false)
  })

  it('is false for the first load, when there was no first message before', () => {
    expect(wasPrepended(undefined, 'a')).toBe(false)
  })

  it('is false when there is nothing to show', () => {
    expect(wasPrepended('a', undefined)).toBe(false)
  })
})

describe('keeping the reader\'s place when older messages are added above', () => {
  it('moves down by the height that was added', () => {
    expect(scrollTopAfterPrepend({ scrollTop: 0, scrollHeight: 1000 }, 3000)).toBe(2000)
  })

  it('keeps a reader who had scrolled a little at the same distance from the content they were reading', () => {
    expect(scrollTopAfterPrepend({ scrollTop: 40, scrollHeight: 1000 }, 2500)).toBe(1540)
  })

  it('does not move when nothing was added', () => {
    expect(scrollTopAfterPrepend({ scrollTop: 300, scrollHeight: 1000 }, 1000)).toBe(300)
  })
})
