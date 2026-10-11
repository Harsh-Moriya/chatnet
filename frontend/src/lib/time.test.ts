import { describe, expect, it } from 'vitest'
import { compareTimestamps } from './time'

// The API writes timestamps like "2026-10-11T13:26:55.494153Z" but drops trailing zeros from the
// fraction, so two values can have fractions of different lengths. Comparing them as plain strings
// gets some pairs wrong, which is what these tests pin down.
describe('compareTimestamps', () => {
  it('orders by the second', () => {
    expect(compareTimestamps('2026-10-11T13:26:55Z', '2026-10-11T13:26:56Z')).toBeLessThan(0)
    expect(compareTimestamps('2026-10-11T13:26:56Z', '2026-10-11T13:26:55Z')).toBeGreaterThan(0)
  })

  it('treats equal timestamps as equal', () => {
    expect(compareTimestamps('2026-10-11T13:26:55.494153Z', '2026-10-11T13:26:55.494153Z')).toBe(0)
  })

  it('orders by the microsecond', () => {
    expect(compareTimestamps('2026-10-11T13:26:55.494153Z', '2026-10-11T13:26:55.494154Z')).toBeLessThan(0)
  })

  it('compares fractions of different lengths by value, not by text', () => {
    // As text, "55.49Z" sorts AFTER "55.494153Z" because 'Z' is greater than '4'. As time, .49 is
    // earlier than .494153.
    expect(compareTimestamps('2026-10-11T13:26:55.49Z', '2026-10-11T13:26:55.494153Z')).toBeLessThan(0)
    expect(compareTimestamps('2026-10-11T13:26:55.494153Z', '2026-10-11T13:26:55.49Z')).toBeGreaterThan(0)
  })

  it('treats a missing fraction as zero', () => {
    expect(compareTimestamps('2026-10-11T13:26:55Z', '2026-10-11T13:26:55.000000Z')).toBe(0)
    expect(compareTimestamps('2026-10-11T13:26:55Z', '2026-10-11T13:26:55.000001Z')).toBeLessThan(0)
  })

  it('treats a trailing zero in the fraction as insignificant', () => {
    expect(compareTimestamps('2026-10-11T13:26:55.5Z', '2026-10-11T13:26:55.500000Z')).toBe(0)
  })

  it('can sort a list into time order', () => {
    const shuffled = [
      '2026-10-11T13:26:55.5Z',
      '2026-10-11T13:26:55.494153Z',
      '2026-10-11T13:26:54Z',
      '2026-10-11T13:26:55.49Z',
    ]

    expect([...shuffled].sort(compareTimestamps)).toEqual([
      '2026-10-11T13:26:54Z',
      '2026-10-11T13:26:55.49Z',
      '2026-10-11T13:26:55.494153Z',
      '2026-10-11T13:26:55.5Z',
    ])
  })
})
