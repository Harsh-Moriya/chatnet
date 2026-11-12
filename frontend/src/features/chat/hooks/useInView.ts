import { useCallback, useEffect, useRef, useState } from 'react'

// Whether an element has come near the screen, once and for good: it starts false and turns true when the element is within a screen's
// height of the viewport, and then stays true. A picture is fetched when it gets near and kept until it is let go with the message.
export function useInView<T extends Element>(margin = '300px'): [(element: T | null) => void, boolean] {
  const [near, setNear] = useState(false)
  const observer = useRef<IntersectionObserver | null>(null)

  useEffect(() => () => observer.current?.disconnect(), [])

  const attach = useCallback(
    (element: T | null) => {
      observer.current?.disconnect()
      if (!element || near) return

      // A browser without IntersectionObserver fetches at once.
      if (typeof IntersectionObserver === 'undefined') {
        setNear(true)
        return
      }

      observer.current = new IntersectionObserver(
        (entries) => {
          if (entries.some((entry) => entry.isIntersecting)) {
            setNear(true)
            observer.current?.disconnect()
          }
        },
        { rootMargin: margin },
      )
      observer.current.observe(element)
    },
    [near, margin],
  )

  return [attach, near]
}
