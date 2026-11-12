import { describe, expect, it } from 'vitest'
import { MAX_TERM_LENGTH, MIN_TERM_LENGTH, SEARCH_PAGE_SIZE, searchTermFrom } from './searchRules'

describe('what the box searches for', () => {
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

  it('is nothing once the text is longer than is searched, and something at exactly the limit', () => {
    expect(searchTermFrom('x'.repeat(MAX_TERM_LENGTH))).toBe('x'.repeat(MAX_TERM_LENGTH))
    expect(searchTermFrom('x'.repeat(MAX_TERM_LENGTH + 1))).toBeNull()
  })

  it('says how short and how long, and the size of a page', () => {
    expect(MIN_TERM_LENGTH).toBe(2)
    expect(MAX_TERM_LENGTH).toBe(100)
    expect(SEARCH_PAGE_SIZE).toBe(20)
  })
})
