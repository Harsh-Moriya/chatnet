// The maths of the picture viewer: how large the picture is on the stage and where it sits. Pure, so it is tested without a browser.

export interface Size {
  width: number
  height: number
}

// A point of the stage, measured from its centre, in stage pixels.
export interface Point {
  x: number
  y: number
}

// How the picture is shown: `scale` is shown pixels for each natural pixel, and (x, y) is where the centre of the picture is, from the
// centre of the stage.
export interface View {
  scale: number
  x: number
  y: number
}

// The two sizes the maths needs: the stage the picture is shown on and the picture as it was made.
export interface Frame {
  stage: Size
  natural: Size
}

// The largest zoom, in shown pixels for each natural pixel. The smallest is the fit.
export const MAX_SCALE = 4

// What one press of a zoom button does.
export const BUTTON_STEP = 1.25

const WHEEL_SENSITIVITY = 0.0015

// The scale at which the whole picture is shown. A picture smaller than the stage is not enlarged.
export function fitScale({ stage, natural }: Frame): number {
  if (natural.width <= 0 || natural.height <= 0 || stage.width <= 0 || stage.height <= 0) return 1
  return Math.min(1, stage.width / natural.width, stage.height / natural.height)
}

export function fitView(frame: Frame): View {
  return { scale: fitScale(frame), x: 0, y: 0 }
}

export const ACTUAL_SIZE: View = { scale: 1, x: 0, y: 0 }

// Keeps the scale between the fit and the largest zoom, and the picture on the stage: along an axis where it is smaller than the stage it is
// centred, and along the other it can be moved only until its edge meets the edge of the stage.
export function clampView(view: View, frame: Frame): View {
  const scale = Math.min(MAX_SCALE, Math.max(fitScale(frame), view.scale))
  // How far the centre of the picture may be from the centre of the stage along an axis: not at all when the picture is the smaller.
  const bound = (offset: number, shown: number, stage: number) => {
    const reach = Math.max(0, (shown - stage) / 2)
    return reach === 0 ? 0 : Math.min(reach, Math.max(-reach, offset))
  }

  return { scale, x: bound(view.x, frame.natural.width * scale, frame.stage.width), y: bound(view.y, frame.natural.height * scale, frame.stage.height) }
}

export function panBy(view: View, dx: number, dy: number, frame: Frame): View {
  return clampView({ ...view, x: view.x + dx, y: view.y + dy }, frame)
}

// Zooms by a factor around a point of the stage, so that the part of the picture under the point stays under it.
export function zoomAt(view: View, factor: number, point: Point, frame: Frame): View {
  const scale = Math.min(MAX_SCALE, Math.max(fitScale(frame), view.scale * factor))
  const ratio = scale / view.scale

  return clampView({ scale, x: point.x - (point.x - view.x) * ratio, y: point.y - (point.y - view.y) * ratio }, frame)
}

// The factor for a turn of the mouse wheel: a turn one way and the same turn the other way cancel out.
export function wheelFactor(deltaY: number): number {
  return Math.exp(-deltaY * WHEEL_SENSITIVITY)
}

// A double click or a double tap: back to the fit when the picture is zoomed in, otherwise in to its actual size (twice its size for a
// picture that already fits at its actual size).
export function toggleZoom(view: View, point: Point, frame: Frame): View {
  const fit = fitScale(frame)
  if (view.scale > fit * 1.01) return fitView(frame)

  return zoomAt(view, (fit < 1 ? 1 : 2) / view.scale, point, frame)
}
