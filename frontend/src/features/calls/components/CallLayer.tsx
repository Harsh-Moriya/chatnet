import { Suspense, lazy } from 'react'
import { useCallStore } from '../callStore'

// The screens are fetched with the first call and not with the page; most sessions never make one.
const CallScreens = lazy(() => import('./CallScreens').then((module) => ({ default: module.CallScreens })))

// Mounted once in the chat layout, so a call shows from any conversation (or none).
export function CallLayer() {
  const calling = useCallStore((state) => state.phase !== 'idle')

  if (!calling) return null

  return (
    <Suspense fallback={null}>
      <CallScreens />
    </Suspense>
  )
}
