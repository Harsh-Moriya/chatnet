import { useSyncExternalStore } from 'react'

function subscribe(onChange: () => void): () => void {
  document.addEventListener('visibilitychange', onChange)
  return () => document.removeEventListener('visibilitychange', onChange)
}

const isVisible = () => document.visibilityState === 'visible'

// Whether this page is in front of the user: false for a background tab or a minimised window.
export function usePageVisible(): boolean {
  return useSyncExternalStore(subscribe, isVisible, () => true)
}
