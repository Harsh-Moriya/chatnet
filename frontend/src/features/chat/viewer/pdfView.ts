// The maths of the PDF viewer: how large the pages are drawn, where each one sits in the scrolled column, and how sharp its canvas may be.
// Pure, so it is tested without a browser.

// The scale of pdf.js at which one point of the page is one CSS pixel is 1; a viewer's "100%" is 96 pixels to the inch, which is 96/72.
export const CSS_UNITS = 96 / 72

// The smallest and the largest scale a page is drawn at, and what one press of a zoom button does.
export const MIN_SCALE = 0.25
export const MAX_SCALE = 5
export const ZOOM_STEP = 1.25

// The most pixels a canvas is given. Browsers refuse a canvas beyond a limit (about 16.7 million pixels on some phones), and a page drawn
// large enough to pass it would come out blank.
export const MAX_CANVAS_PIXELS = 16_000_000

export interface Size {
  width: number
  height: number
}

export function clampScale(scale: number): number {
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale))
}

// The scale at which a page of this width (in points, at scale 1) fills the width of the stage less a margin on each side.
export function fitWidthScale(pageWidth: number, stageWidth: number, margin: number): number {
  if (pageWidth <= 0) return clampScale(CSS_UNITS)
  return clampScale(Math.max(0, stageWidth - 2 * margin) / pageWidth)
}

export function stepScale(scale: number, direction: 1 | -1): number {
  return clampScale(scale * ZOOM_STEP ** direction)
}

// The zoom as a person reads it: 100% is 96 pixels to the inch.
export function percentOf(scale: number): number {
  return Math.round((scale / CSS_UNITS) * 100)
}

// The distance from the top of the column to the top of each page, with `gap` above the first and between the pages.
export function pageOffsets(heights: readonly number[], gap: number): number[] {
  const offsets: number[] = []
  let top = gap
  for (const height of heights) {
    offsets.push(top)
    top += height + gap
  }
  return offsets
}

// The height of the whole column.
export function columnHeight(heights: readonly number[], gap: number): number {
  return heights.reduce((sum, height) => sum + height + gap, gap)
}

// A place in the column that survives a change of scale: a page and how far down it, from 0 to 1.
export interface Place {
  index: number
  fraction: number
}

// The place under a line of the column, which is `y` from its top. A line in the gap under a page, above the first or below the last is
// the nearest page's edge.
export function locate(offsets: readonly number[], heights: readonly number[], y: number): Place {
  if (offsets.length === 0) return { index: 0, fraction: 0 }

  let index = 0
  for (let i = 0; i < offsets.length; i++) {
    if (offsets[i] <= y) index = i
    else break
  }

  const fraction = heights[index] > 0 ? (y - offsets[index]) / heights[index] : 0
  return { index, fraction: Math.min(1, Math.max(0, fraction)) }
}

// The line of the column a place is at.
export function lineOf(offsets: readonly number[], heights: readonly number[], place: Place): number {
  const index = Math.min(Math.max(0, place.index), offsets.length - 1)
  return offsets[index] + heights[index] * place.fraction
}

// The scale of the canvas of a page drawn at `width` by `height` CSS pixels: the screen's pixel ratio, lowered when that would pass the most
// pixels a canvas may have.
export function outputScale(pixelRatio: number, size: Size, maxPixels = MAX_CANVAS_PIXELS): number {
  const area = size.width * size.height
  if (area <= 0) return pixelRatio

  return Math.min(pixelRatio, Math.sqrt(maxPixels / area))
}
