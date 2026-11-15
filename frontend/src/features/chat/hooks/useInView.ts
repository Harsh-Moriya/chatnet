import { useCallback, useEffect, useRef, useState } from 'react'

// Whether an element has come near the screen. It starts false and turns true when the element is within `margin` of the viewport, and then
// stays true: a picture is fetched when it gets near and kept until it is let go with the message. With `release` it follows the element
// instead and turns false again when it is far away, for a page of a document that is drawn only while it is near.
//
// An element inside a scrolling box is clipped by the box, so the margin does not reach past it when the screen is what it is measured
// from: the margin only means something measured from the box. `root` is that box. Undefined measures from the screen; null is a box that is
// not there yet (the element is not watched until it is).
export function useInView<T extends Element>(
  margin = '300px',
  release = false,
  root?: Element | null,
): [(element: T | null) => void, boolean] {
  const [near, setNear] = useState(false)
  const observer = useRef<IntersectionObserver | null>(null)
  // Set once a flag that stays has turned true, so the observer is not made again.
  const stayed = useRef(false)

  useEffect(() => () => observer.current?.disconnect(), [])

  const attach = useCallback(
    (element: T | null) => {
      observer.current?.disconnect()
      if (!element || stayed.current || root === null) return

      // A browser without IntersectionObserver fetches at once.
      if (typeof IntersectionObserver === 'undefined') {
        stayed.current = true
        setNear(true)
        return
      }

      observer.current = new IntersectionObserver(
        (entries) => {
          if (release) {
            setNear(entries[entries.length - 1].isIntersecting)
          } else if (entries.some((entry) => entry.isIntersecting)) {
            stayed.current = true
            setNear(true)
            observer.current?.disconnect()
          }
        },
        { root, rootMargin: margin },
      )
      observer.current.observe(element)
    },
    [margin, release, root],
  )

  return [attach, near]
}
