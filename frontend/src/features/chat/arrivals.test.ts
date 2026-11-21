import { describe, expect, it } from 'vitest'
import { arrivingKeys } from './arrivals'

const keys = (previous: string[] | null, current: string[]) => [...arrivingKeys(previous, current)]

describe('which items are new arrivals', () => {
  it('is nothing the first time a conversation is drawn, so its history does not animate', () => {
    expect(keys(null, ['a', 'b', 'c'])).toEqual([])
  })

  it('is nothing while the first messages are still on their way', () => {
    expect(keys([], ['a', 'b'])).toEqual([])
  })

  it('is a message added at the end', () => {
    expect(keys(['a', 'b'], ['a', 'b', 'c'])).toEqual(['c'])
  })

  it('is every message added at once at the end', () => {
    expect(keys(['a', 'b'], ['a', 'b', 'c', 'd'])).toEqual(['c', 'd'])
  })

  it('is nothing when nothing changed', () => {
    expect(keys(['a', 'b'], ['a', 'b'])).toEqual([])
  })

  it('is not older messages added above, a page of history', () => {
    expect(keys(['c', 'd'], ['a', 'b', 'c', 'd'])).toEqual([])
  })

  it('is only what is new at the end when older messages arrive at the same time', () => {
    expect(keys(['c', 'd'], ['a', 'b', 'c', 'd', 'e'])).toEqual(['e'])
  })

  it('is nothing when what was shown is gone, a different window of the conversation', () => {
    expect(keys(['a', 'b'], ['x', 'y', 'z'])).toEqual([])
  })

  it('is nothing for an item that was already known, such as a message that moved', () => {
    expect(keys(['a', 'b', 'c'], ['a', 'c', 'b'])).toEqual([])
  })

  it('does not count a message again when it goes from unconfirmed to confirmed under the same key', () => {
    expect(keys(['a', 'pending-1'], ['a', 'pending-1'])).toEqual([])
  })
})
