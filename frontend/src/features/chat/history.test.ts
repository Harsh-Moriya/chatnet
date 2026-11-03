import { describe, expect, it } from 'vitest'
import { HISTORY_PAGE_SIZE, followsEnd, justSent, olderCursor, returnsToEnd, scrollTopAfterPrepend, wasPrepended } from './history'
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

describe('following the end of the list', () => {
  it('follows new content for a reader who is at the bottom', () => {
    expect(followsEnd({ atBottom: true, inWindow: false, justSent: false })).toBe(true)
  })

  it('leaves a reader who scrolled away where they are', () => {
    expect(followsEnd({ atBottom: false, inWindow: false, justSent: false })).toBe(false)
  })

  it('never follows in a window of the history, whose bottom is not the bottom of the conversation', () => {
    expect(followsEnd({ atBottom: true, inWindow: true, justSent: false })).toBe(false)
  })

  it('always follows a message the reader just sent, in a window or not, scrolled away or not', () => {
    expect(followsEnd({ atBottom: false, inWindow: false, justSent: true })).toBe(true)
    expect(followsEnd({ atBottom: false, inWindow: true, justSent: true })).toBe(true)
  })
})

describe('a message the reader just sent', () => {
  const pending = { kind: 'pending', key: 'client-1' }

  it('is a pending message that was not already the last item', () => {
    expect(justSent('m9', pending)).toBe(true)
    expect(justSent(undefined, pending)).toBe(true)
  })

  it('is not one that was already last: a failed message waiting for Retry must not pull the reader to the end every time the list changes', () => {
    expect(justSent('client-1', pending)).toBe(false)
  })

  it('is not a message that arrived, nor a day label, nor an empty list', () => {
    expect(justSent('m8', { kind: 'message', key: 'm9' })).toBe(false)
    expect(justSent('m8', { kind: 'day', key: 'day:2026-10-11' })).toBe(false)
    expect(justSent('m8', undefined)).toBe(false)
  })
})

describe('returning to the end after leaving a window', () => {
  it('shows the newest messages from their end when the window was dropped and the list emptied, as the jump to the latest messages and sending from a window do', () => {
    expect(returnsToEnd({ wasInWindow: true, inWindow: false, messageCount: 0 })).toBe(true)
  })

  it('leaves a reader alone when the window merely reached the newest message and kept what it held', () => {
    expect(returnsToEnd({ wasInWindow: true, inWindow: false, messageCount: 140 })).toBe(false)
  })

  it('does nothing while reading a window, or when there never was one', () => {
    expect(returnsToEnd({ wasInWindow: true, inWindow: true, messageCount: 0 })).toBe(false)
    expect(returnsToEnd({ wasInWindow: false, inWindow: false, messageCount: 0 })).toBe(false)
  })
})
