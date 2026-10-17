import { describe, expect, it } from 'vitest'
import { conversationsKey } from './conversationsKey'

describe('conversationsKey', () => {
  it('is empty when the user is in no conversation', () => {
    expect(conversationsKey([])).toBe('')
  })

  it('does not depend on the order the conversations were added in', () => {
    expect(conversationsKey(['c2', 'a1', 'b3'])).toBe(conversationsKey(['a1', 'b3', 'c2']))
  })

  it('changes when a conversation is added', () => {
    expect(conversationsKey(['a1', 'b3'])).not.toBe(conversationsKey(['a1', 'b3', 'c2']))
  })

  it('changes when one conversation is replaced by another of the same number', () => {
    expect(conversationsKey(['a1', 'b3'])).not.toBe(conversationsKey(['a1', 'c2']))
  })

  it('does not confuse two sets whose ids would read the same when run together', () => {
    expect(conversationsKey(['ab', 'c'])).not.toBe(conversationsKey(['a', 'bc']))
  })

  it('does not change the list it was given', () => {
    const ids = ['c2', 'a1']

    conversationsKey(ids)

    expect(ids).toEqual(['c2', 'a1'])
  })
})
