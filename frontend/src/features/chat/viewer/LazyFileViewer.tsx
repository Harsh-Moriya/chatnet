import { Suspense, lazy, useEffect, useState } from 'react'
import { useViewerStore } from './viewerStore'

// Opened by a click and not needed to show a conversation: the viewer is fetched when a file is first opened, and not with the page.
const FileViewer = lazy(() => import('./FileViewer').then((module) => ({ default: module.FileViewer })))

// Mounted from the first time a file of this conversation is opened, so closing the viewer returns focus to what opened it. A viewer left
// open when the conversation is left is closed.
export function LazyFileViewer({ conversationId }: { conversationId: string }) {
  const wanted = useViewerStore((state) => state.target?.conversationId === conversationId)
  const [mounted, setMounted] = useState(wanted)
  if (wanted && !mounted) setMounted(true)

  useEffect(() => () => useViewerStore.getState().close(), [])

  return mounted ? (
    <Suspense fallback={null}>
      <FileViewer conversationId={conversationId} />
    </Suspense>
  ) : null
}
