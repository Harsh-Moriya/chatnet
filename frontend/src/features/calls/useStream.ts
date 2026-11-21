import { useCallback } from 'react'

// A stream is attached to an <audio> or <video> through a property, not an attribute. A callback ref sets it whenever the element appears or
// the stream changes: the picture on the call screen is a new element each time the screen is brought back from the bar, and an effect that
// runs only when the stream changes would leave it empty.
export function useStream<T extends HTMLMediaElement>(stream: MediaStream | null) {
  return useCallback(
    (element: T | null) => {
      if (element) element.srcObject = stream
    },
    [stream],
  )
}
