import { describe, expect, it } from 'vitest'
import { deletedStored, sealedStored, stored } from '../testKit'
import { mergeReadPoint, newestOfEach, supersedes } from './rules'
import type { ReadPoint } from './types'

describe('which copy of a message is kept', () => {
  it('keeps the first copy it is given', () => {
    expect(supersedes(stored({ changeSeq: 3 }), undefined)).toBe(true)
  })

  it('takes a copy with a higher change number over the one held', () => {
    expect(supersedes(stored({ changeSeq: 4 }), stored({ changeSeq: 3 }))).toBe(true)
  })

  it('never takes a copy with a lower change number over the one held', () => {
    expect(supersedes(stored({ changeSeq: 2 }), stored({ changeSeq: 3 }))).toBe(false)
  })

  it('does not replace a copy with another of the same number', () => {
    expect(supersedes(stored({ changeSeq: 3, text: 'same' }), stored({ changeSeq: 3, text: 'same' }))).toBe(false)
  })

  it('does not bring back a message that was deleted, whatever late copy arrives', () => {
    const deleted = deletedStored({ changeSeq: 5 })

    expect(supersedes(stored({ changeSeq: 4 }), deleted)).toBe(false)
    expect(supersedes(stored({ changeSeq: 5 }), deleted)).toBe(false)
  })

  it('takes a deletion over the text it deletes', () => {
    expect(supersedes(deletedStored({ changeSeq: 5 }), stored({ changeSeq: 4 }))).toBe(true)
  })

  it('takes the opened copy over the sealed one of the same number, which is how a message is opened later', () => {
    expect(supersedes(stored({ changeSeq: 3 }), sealedStored({ changeSeq: 3 }))).toBe(true)
  })

  it('does not take a sealed copy over an opened one of the same number', () => {
    expect(supersedes(sealedStored({ changeSeq: 3 }), stored({ changeSeq: 3 }))).toBe(false)
  })

  it('takes a deletion over the sealed copy of the same number, and never the sealed copy over a deletion', () => {
    expect(supersedes(deletedStored({ changeSeq: 3 }), sealedStored({ changeSeq: 3 }))).toBe(true)
    expect(supersedes(sealedStored({ changeSeq: 3 }), deletedStored({ changeSeq: 3 }))).toBe(false)
  })
})

describe('the copies of one page', () => {
  it('holds each message once, as the newest copy of it', () => {
    const first = stored({ id: 'm1', changeSeq: 2, text: 'old' })
    const later = stored({ id: 'm1', changeSeq: 5, text: 'new' })
    const other = stored({ id: 'm2', changeSeq: 1 })

    const page = newestOfEach([first, other, later])

    expect(page.map((m) => m.id).sort()).toEqual(['m1', 'm2'])
    expect(page.find((m) => m.id === 'm1')).toBe(later)
  })

  it('keeps the newest copy whichever order they come in', () => {
    const old = stored({ id: 'm1', changeSeq: 2 })
    const newer = stored({ id: 'm1', changeSeq: 5 })

    expect(newestOfEach([newer, old])).toEqual([newer])
  })

  it('does not take messages of two conversations that share an id for one', () => {
    const page = newestOfEach([stored({ id: 'm1', conversationId: 'c1' }), stored({ id: 'm1', conversationId: 'c2' })])

    expect(page).toHaveLength(2)
  })
})

describe('how far a conversation has been read', () => {
  const held = (overrides: Partial<ReadPoint> = {}): ReadPoint => ({
    userId: 'u',
    conversationId: 'c',
    cursor: 10,
    startSeq: 8,
    oldestId: 'm5',
    historyRead: true,
    ...overrides,
  })

  it('starts from nothing', () => {
    expect(mergeReadPoint(undefined, 'u', 'c', { startSeq: 7, oldestId: 'm9', historyRead: false, cursor: null })).toEqual({
      userId: 'u',
      conversationId: 'c',
      cursor: null,
      startSeq: 7,
      oldestId: 'm9',
      historyRead: false,
    })
  })

  it('moves the cursor forward', () => {
    expect(mergeReadPoint(held(), 'u', 'c', { cursor: 14 }).cursor).toBe(14)
  })

  it('never moves the cursor back, so a slower tab cannot undo a faster one', () => {
    expect(mergeReadPoint(held({ cursor: 10 }), 'u', 'c', { cursor: 6 }).cursor).toBe(10)
  })

  it('takes a cursor when there was none, and keeps one when the change has none', () => {
    expect(mergeReadPoint(held({ cursor: null }), 'u', 'c', { cursor: 4 }).cursor).toBe(4)
    expect(mergeReadPoint(held({ cursor: 9 }), 'u', 'c', { cursor: null }).cursor).toBe(9)
    expect(mergeReadPoint(held({ cursor: 9 }), 'u', 'c', {}).cursor).toBe(9)
  })

  it('keeps the first starting number, so reading again goes back to the same point', () => {
    expect(mergeReadPoint(held({ startSeq: 8 }), 'u', 'c', { startSeq: 20 }).startSeq).toBe(8)
    expect(mergeReadPoint(held({ startSeq: null }), 'u', 'c', { startSeq: 20 }).startSeq).toBe(20)
  })

  it('follows the oldest message read, and keeps it when the change names none', () => {
    expect(mergeReadPoint(held({ oldestId: 'm5' }), 'u', 'c', { oldestId: 'm2' }).oldestId).toBe('m2')
    expect(mergeReadPoint(held({ oldestId: 'm5' }), 'u', 'c', {}).oldestId).toBe('m5')
  })

  it('never takes back that the history was read', () => {
    expect(mergeReadPoint(held({ historyRead: true }), 'u', 'c', { historyRead: false }).historyRead).toBe(true)
    expect(mergeReadPoint(held({ historyRead: false }), 'u', 'c', { historyRead: true }).historyRead).toBe(true)
    expect(mergeReadPoint(held({ historyRead: false }), 'u', 'c', {}).historyRead).toBe(false)
  })
})
