import { describe, expect, it } from 'vitest'
import { shiftIntoView } from './tooltipPosition'

describe('keeping a tooltip inside the window', () => {
  it('leaves one that fits where it is', () => {
    expect(shiftIntoView({ left: 100, right: 260 }, 1000)).toBe(0)
  })

  it('moves one that runs off the right edge back by what is outside, plus the margin', () => {
    expect(shiftIntoView({ left: 900, right: 1040 }, 1000)).toBe(-48)
  })

  it('moves one that runs off the left edge forward by what is outside, plus the margin', () => {
    expect(shiftIntoView({ left: -30, right: 110 }, 1000)).toBe(38)
  })

  it('treats a tooltip that only touches the margin as fitting', () => {
    expect(shiftIntoView({ left: 8, right: 992 }, 1000)).toBe(0)
  })
})
