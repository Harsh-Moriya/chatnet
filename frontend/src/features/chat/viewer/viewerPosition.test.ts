import { describe, expect, it } from 'vitest'
import { PREFETCH_DISTANCE, positionLabel, shouldFetchMore } from './viewerPosition'

describe('what the counter of the viewer says', () => {
  it('counts from one', () => {
    expect(positionLabel(0, 12, false)).toBe('1 of 12')
    expect(positionLabel(11, 12, false)).toBe('12 of 12')
  })

  it('says the list goes on while more pages are to come', () => {
    expect(positionLabel(2, 50, true)).toBe('3 of 50+')
  })
})

describe('when the viewer asks for the next page', () => {
  it('asks once the file shown is within the distance of the last one loaded', () => {
    const loaded = 50

    expect(shouldFetchMore(loaded - PREFETCH_DISTANCE, loaded, true, false)).toBe(true)
    expect(shouldFetchMore(loaded - 1, loaded, true, false)).toBe(true)
  })

  it('waits while the file shown is further from the end', () => {
    const loaded = 50

    expect(shouldFetchMore(loaded - PREFETCH_DISTANCE - 1, loaded, true, false)).toBe(false)
  })

  it('does not ask when the list has no more, or a request is already out', () => {
    expect(shouldFetchMore(49, 50, false, false)).toBe(false)
    expect(shouldFetchMore(49, 50, true, true)).toBe(false)
  })

  it('does not ask for a file that is not in the list', () => {
    expect(shouldFetchMore(-1, 3, true, false)).toBe(false)
  })
})
