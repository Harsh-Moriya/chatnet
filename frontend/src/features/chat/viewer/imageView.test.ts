import { describe, expect, it } from 'vitest'
import { ACTUAL_SIZE, BUTTON_STEP, type Frame, MAX_SCALE, type View, clampView, fitScale, fitView, panBy, toggleZoom, wheelFactor, zoomAt } from './imageView'

// A stage of 1000 by 800 and a picture of 2000 by 1600: it fits at half its size.
const BIG: Frame = { stage: { width: 1000, height: 800 }, natural: { width: 2000, height: 1600 } }
// A picture of 400 by 300 on the same stage: it fits at its own size.
const SMALL: Frame = { stage: { width: 1000, height: 800 }, natural: { width: 400, height: 300 } }

describe('the scale at which the whole picture is shown', () => {
  it('is bound by the width for a picture that is wide for the stage', () => {
    expect(fitScale({ stage: { width: 1000, height: 800 }, natural: { width: 4000, height: 1000 } })).toBe(0.25)
  })

  it('is bound by the height for a picture that is tall for the stage', () => {
    expect(fitScale({ stage: { width: 1000, height: 800 }, natural: { width: 1000, height: 3200 } })).toBe(0.25)
  })

  it('never enlarges a picture that is smaller than the stage', () => {
    expect(fitScale(SMALL)).toBe(1)
  })

  it.each([
    [{ width: 0, height: 800 }, { width: 100, height: 100 }],
    [{ width: 1000, height: 800 }, { width: 0, height: 100 }],
  ])('is 1 while a size is not known yet: stage %j, picture %j', (stage, natural) => {
    expect(fitScale({ stage, natural })).toBe(1)
  })
})

describe('keeping the picture on the stage', () => {
  it('centres a picture that is smaller than the stage, whatever it was asked for', () => {
    expect(clampView({ scale: 1, x: 300, y: -200 }, SMALL)).toEqual({ scale: 1, x: 0, y: 0 })
  })

  it('lets a picture larger than the stage move only until its edge meets the edge of the stage', () => {
    // At 100% the picture is 2000 by 1600 on 1000 by 800: 500 and 400 can be seen beyond each side.
    expect(clampView({ scale: 1, x: 9999, y: -9999 }, BIG)).toEqual({ scale: 1, x: 500, y: -400 })
    expect(clampView({ scale: 1, x: 120, y: -80 }, BIG)).toEqual({ scale: 1, x: 120, y: -80 })
  })

  it('centres along the axis where the zoomed picture is still smaller than the stage and moves along the other', () => {
    // A wide picture: 3000 by 400 on 1000 by 800 fits at one third; at 1 it is wider than the stage and still lower than it.
    const wide: Frame = { stage: { width: 1000, height: 800 }, natural: { width: 3000, height: 400 } }

    expect(clampView({ scale: 1, x: 5000, y: 300 }, wide)).toEqual({ scale: 1, x: 1000, y: 0 })
  })

  it('keeps the scale between the fit and the largest zoom', () => {
    expect(clampView({ scale: 0.1, x: 0, y: 0 }, BIG).scale).toBe(0.5)
    expect(clampView({ scale: 40, x: 0, y: 0 }, BIG).scale).toBe(MAX_SCALE)
  })

  it('moves by what it is asked, and no further than it may', () => {
    const zoomed: View = { scale: 1, x: 100, y: 0 }

    expect(panBy(zoomed, 50, 20, BIG)).toEqual({ scale: 1, x: 150, y: 20 })
    expect(panBy(zoomed, 1000, 0, BIG).x).toBe(500)
  })
})

describe('zooming around a point', () => {
  it('keeps the part of the picture under the point under it', () => {
    const point = { x: 100, y: 50 }
    const before = fitView(BIG)
    // What is under the point, in pixels of the picture measured from its centre.
    const under = { x: (point.x - before.x) / before.scale, y: (point.y - before.y) / before.scale }

    const after = zoomAt(before, 2, point, BIG)

    expect(after.scale).toBe(1)
    expect(after.x + under.x * after.scale).toBeCloseTo(point.x)
    expect(after.y + under.y * after.scale).toBeCloseTo(point.y)
  })

  it('does not go beyond the largest zoom or below the fit', () => {
    expect(zoomAt(fitView(BIG), 100, { x: 0, y: 0 }, BIG).scale).toBe(MAX_SCALE)
    expect(zoomAt({ scale: 1, x: 0, y: 0 }, 0.001, { x: 0, y: 0 }, BIG)).toEqual(fitView(BIG))
  })

  it('zooms in and out again to where it was, around the centre', () => {
    const zoomedIn = zoomAt(fitView(BIG), BUTTON_STEP, { x: 0, y: 0 }, BIG)

    expect(zoomAt(zoomedIn, 1 / BUTTON_STEP, { x: 0, y: 0 }, BIG).scale).toBeCloseTo(fitScale(BIG))
  })

  it('does not take the picture off the stage when the point is at the edge', () => {
    const view = zoomAt(fitView(BIG), 2, { x: 500, y: 400 }, BIG)

    expect(view).toEqual(clampView(view, BIG))
    expect(view.x).toBeLessThanOrEqual(500)
  })

  it('turns a turn of the wheel one way and the same turn the other way into factors that cancel out', () => {
    expect(wheelFactor(0)).toBe(1)
    expect(wheelFactor(-100)).toBeGreaterThan(1)
    expect(wheelFactor(100)).toBeLessThan(1)
    expect(wheelFactor(-100) * wheelFactor(100)).toBeCloseTo(1)
  })
})

describe('a double click or a double tap', () => {
  it('goes in to the actual size for a picture that is shown smaller than it is', () => {
    expect(toggleZoom(fitView(BIG), { x: 0, y: 0 }, BIG)).toEqual(ACTUAL_SIZE)
  })

  it('goes back to the fit when the picture is zoomed in', () => {
    expect(toggleZoom({ scale: 1, x: 120, y: 30 }, { x: 0, y: 0 }, BIG)).toEqual(fitView(BIG))
  })

  it('goes to twice the size for a picture that already fits at its own size, and back', () => {
    const zoomed = toggleZoom(fitView(SMALL), { x: 0, y: 0 }, SMALL)

    expect(zoomed.scale).toBe(2)
    expect(toggleZoom(zoomed, { x: 0, y: 0 }, SMALL)).toEqual(fitView(SMALL))
  })

  it('counts a picture that is a hair above the fit as fitting', () => {
    expect(toggleZoom({ scale: 0.5 * 1.005, x: 0, y: 0 }, { x: 0, y: 0 }, BIG).scale).toBe(1)
  })
})
