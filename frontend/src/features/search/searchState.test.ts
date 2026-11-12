import { describe, expect, it } from 'vitest'
import { viewOf, type SearchFacts } from './searchState'

const facts = (overrides: Partial<SearchFacts> = {}): SearchFacts => ({
  typed: 'needle',
  hasTerms: true,
  unavailable: false,
  historyPending: 0,
  hitCount: 0,
  failed: false,
  settled: false,
  ...overrides,
})

describe('what the search shows', () => {
  it('asks for more to be typed while there is too little', () => {
    expect(viewOf(facts({ typed: null }))).toEqual({ kind: 'short' })
  })

  it('says there is nothing to look for in text that is only marks or words to keep out', () => {
    expect(viewOf(facts({ hasTerms: false }))).toEqual({ kind: 'nothing' })
  })

  it('says search is not available where the browser cannot keep the messages, whatever is typed', () => {
    expect(viewOf(facts({ unavailable: true }))).toEqual({ kind: 'unavailable' })
    expect(viewOf(facts({ unavailable: true, typed: null }))).toEqual({ kind: 'unavailable' })
  })

  it('says it is searching until the search has finished', () => {
    expect(viewOf(facts())).toEqual({ kind: 'searching' })
  })

  it('shows the hits as soon as there are some, even while it is still searching', () => {
    expect(viewOf(facts({ hitCount: 3 }))).toEqual({ kind: 'hits', stillReading: false })
    expect(viewOf(facts({ hitCount: 3, settled: true }))).toEqual({ kind: 'hits', stillReading: false })
  })

  it('says nothing was found once the search has finished and found none', () => {
    expect(viewOf(facts({ settled: true }))).toEqual({ kind: 'empty', stillReading: false })
  })

  it('says the search failed when it did and there is nothing to show', () => {
    expect(viewOf(facts({ failed: true }))).toEqual({ kind: 'failed' })
  })

  it('keeps the hits it had when a later page fails', () => {
    expect(viewOf(facts({ failed: true, hitCount: 2 }))).toEqual({ kind: 'hits', stillReading: false })
  })

  it('says older messages are still being read, with or without hits', () => {
    expect(viewOf(facts({ historyPending: 2, settled: true }))).toEqual({ kind: 'empty', stillReading: true })
    expect(viewOf(facts({ historyPending: 1, hitCount: 4 }))).toEqual({ kind: 'hits', stillReading: true })
  })

  it('does not say older messages are being read while it is still searching', () => {
    expect(viewOf(facts({ historyPending: 2 }))).toEqual({ kind: 'searching' })
  })
})
