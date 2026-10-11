import { describe, expect, it } from 'vitest'
import { typingLabel } from './typingLabel'

const user = (name: string) => ({ userId: name.toLowerCase(), displayName: name })

describe('typingLabel', () => {
  it('has nothing to say when nobody is typing', () => {
    expect(typingLabel(undefined)).toBeNull()
    expect(typingLabel([])).toBeNull()
  })

  it('names one person', () => {
    expect(typingLabel([user('Bob')])).toBe('Bob is typing')
  })

  it('names two people', () => {
    expect(typingLabel([user('Bob'), user('Carol')])).toBe('Bob and Carol are typing')
  })

  it('does not list a crowd', () => {
    expect(typingLabel([user('Bob'), user('Carol'), user('Dave')])).toBe('Several people are typing')
    expect(typingLabel([user('A'), user('B'), user('C'), user('D'), user('E')])).toBe('Several people are typing')
  })
})
