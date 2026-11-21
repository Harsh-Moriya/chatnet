// How far (in px, positive to the right) a tooltip centred on its control has to move to stay inside the window with `margin` to spare.
// A control at the edge of a header would otherwise have half of its tooltip cut off.
export function shiftIntoView(box: { left: number; right: number }, viewportWidth: number, margin = 8): number {
  if (box.right > viewportWidth - margin) return viewportWidth - margin - box.right
  if (box.left < margin) return margin - box.left
  return 0
}
