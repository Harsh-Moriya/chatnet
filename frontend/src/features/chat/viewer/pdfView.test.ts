import { describe, expect, it } from 'vitest'
import { CSS_UNITS, MAX_SCALE, MIN_SCALE, ZOOM_STEP, clampScale, columnHeight, fitWidthScale, lineOf, locate, outputScale, pageOffsets, percentOf, stepScale } from './pdfView'

describe('how large the pages are drawn', () => {
  it('fills the width of the stage less a margin on each side', () => {
    // A page of 600 points on a stage of 1000 pixels with 20 pixels of margin: 960 / 600.
    expect(fitWidthScale(600, 1000, 20)).toBeCloseTo(1.6)
  })

  it('does not go beyond the smallest or the largest scale', () => {
    expect(fitWidthScale(600, 100, 20)).toBe(MIN_SCALE)
    expect(fitWidthScale(100, 4000, 0)).toBe(MAX_SCALE)
    expect(clampScale(0.01)).toBe(MIN_SCALE)
    expect(clampScale(50)).toBe(MAX_SCALE)
  })

  it('has a usable scale before the size of a page is known, or when there is no room', () => {
    expect(fitWidthScale(0, 1000, 20)).toBeCloseTo(CSS_UNITS)
    expect(fitWidthScale(600, 30, 20)).toBe(MIN_SCALE)
  })

  it('steps by the same factor each way, and stops at the limits', () => {
    expect(stepScale(1, 1)).toBeCloseTo(ZOOM_STEP)
    expect(stepScale(stepScale(1, 1), -1)).toBeCloseTo(1)
    expect(stepScale(MAX_SCALE, 1)).toBe(MAX_SCALE)
    expect(stepScale(MIN_SCALE, -1)).toBe(MIN_SCALE)
  })

  it('reads the zoom with 100% at 96 pixels to the inch', () => {
    expect(percentOf(CSS_UNITS)).toBe(100)
    expect(percentOf(2 * CSS_UNITS)).toBe(200)
    expect(percentOf(MIN_SCALE)).toBe(19)
  })
})

describe('where the pages are in the column', () => {
  const heights = [100, 200, 100]

  it('puts the gap above the first page and between the pages', () => {
    expect(pageOffsets(heights, 10)).toEqual([10, 120, 330])
  })

  it('adds up the height of the column, with a gap under the last page too', () => {
    expect(columnHeight(heights, 10)).toBe(440)
    expect(columnHeight([], 10)).toBe(10)
  })

  const offsets = pageOffsets(heights, 10)

  it('says which page is under a line, and how far down it', () => {
    expect(locate(offsets, heights, 60)).toEqual({ index: 0, fraction: 0.5 })
    expect(locate(offsets, heights, 120)).toEqual({ index: 1, fraction: 0 })
    expect(locate(offsets, heights, 320)).toEqual({ index: 1, fraction: 1 })
  })

  it('gives the page above for a line in the gap under it, and the nearest end outside the column', () => {
    // 220 to 330 is the gap under the second page.
    expect(locate(offsets, heights, 325)).toEqual({ index: 1, fraction: 1 })
    expect(locate(offsets, heights, 0)).toEqual({ index: 0, fraction: 0 })
    expect(locate(offsets, heights, 5000)).toEqual({ index: 2, fraction: 1 })
  })

  it('has a place even before there are pages', () => {
    expect(locate([], [], 50)).toEqual({ index: 0, fraction: 0 })
  })

  it('finds the line of a place again after the pages have changed size', () => {
    const place = locate(offsets, heights, 220)
    const larger = [200, 400, 200]

    // Halfway down the second page, which now starts at 220 and is 400 tall.
    expect(place).toEqual({ index: 1, fraction: 0.5 })
    expect(lineOf(pageOffsets(larger, 10), larger, place)).toBe(420)
  })

  it('keeps a place on the last page when a line is asked for past it', () => {
    expect(lineOf(offsets, heights, { index: 9, fraction: 0.5 })).toBe(offsets[2] + 50)
  })
})

describe('how sharp a canvas may be', () => {
  it('uses the pixel ratio of the screen when the canvas stays small enough', () => {
    expect(outputScale(2, { width: 800, height: 1000 })).toBe(2)
  })

  it('lowers it so that the canvas does not pass the most pixels it may have', () => {
    const size = { width: 4000, height: 5000 }

    const scale = outputScale(2, size, 16_000_000)

    expect(scale).toBeLessThan(2)
    expect(size.width * scale * (size.height * scale)).toBeLessThanOrEqual(16_000_001)
  })

  it('keeps the pixel ratio for a page with no size yet', () => {
    expect(outputScale(2, { width: 0, height: 0 })).toBe(2)
  })
})
