import { describe, expect, it } from 'vitest'
import { MAX_TERM_LENGTH, MIN_TERM_LENGTH, SEARCH_PAGE_SIZE, nextSearchCursor, searchTermFrom } from './searchRules'
import type { SearchHit } from './types'

function hit(id: string): SearchHit {
  return {
    messageId: id,
    conversationId: 'c1',
    conversationName: 'Bob',
    conversationType: 'Direct',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    createdAt: '2026-10-11T10:00:00Z',
    snippet: [{ text: id, match: true }],
    fileName: null,
  }
}

const page = (size: number) => Array.from({ length: size }, (_, i) => hit(`h${i}`))

describe('what the box sends as a search', () => {
  it('is the text without the spaces around it', () => {
    expect(searchTermFrom('  needle  ')).toBe('needle')
  })

  it('keeps the spaces inside, which are part of what is looked for', () => {
    expect(searchTermFrom('blue  print')).toBe('blue  print')
  })

  it('is nothing while the text is too short, counting without the spaces around it', () => {
    expect(searchTermFrom('')).toBeNull()
    expect(searchTermFrom('   ')).toBeNull()
    expect(searchTermFrom('n')).toBeNull()
    expect(searchTermFrom(' n ')).toBeNull()
    expect(searchTermFrom('ne')).toBe('ne')
  })

  it('is nothing once the text is longer than the server accepts, and accepted at exactly the limit', () => {
    expect(searchTermFrom('x'.repeat(MAX_TERM_LENGTH))).toBe('x'.repeat(MAX_TERM_LENGTH))
    expect(searchTermFrom('x'.repeat(MAX_TERM_LENGTH + 1))).toBeNull()
    expect(searchTermFrom(`  ${'x'.repeat(MAX_TERM_LENGTH)}  `)).toBe('x'.repeat(MAX_TERM_LENGTH))
  })

  it('has limits that match the server rule of 2 to 100 characters', () => {
    expect(MIN_TERM_LENGTH).toBe(2)
    expect(MAX_TERM_LENGTH).toBe(100)
  })
})

describe('the cursor for more hits', () => {
  it('is the last hit of a full page, because the server continues after it', () => {
    const full = page(SEARCH_PAGE_SIZE)

    expect(nextSearchCursor(full)).toBe(full[SEARCH_PAGE_SIZE - 1].messageId)
  })

  it('is nothing for a page that is short, because the last one was reached', () => {
    expect(nextSearchCursor(page(SEARCH_PAGE_SIZE - 1))).toBeUndefined()
    expect(nextSearchCursor([])).toBeUndefined()
  })

  it('has the page size the server uses', () => {
    expect(SEARCH_PAGE_SIZE).toBe(20)
  })
})
