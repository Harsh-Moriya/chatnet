import { useCallback, useEffect, useRef, useState } from 'react'
import { fileObjects } from '../files/openFiles'
import type { FileSource } from '../files/fileObjects'

export type FileObjectState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'ready'; url: string }
  | { status: 'failed' }

// A picture or a video of a message, opened. Nothing is fetched until `enabled` is true (a picture that is near the screen, a video that was
// asked for), the file is held for as long as the component shows it, and a failure can be tried again with `retry`. A file that is not drawn
// stays idle.
export function useFileObject(source: FileSource | null, enabled: boolean): [FileObjectState, () => void] {
  const [state, setState] = useState<FileObjectState>({ status: 'idle' })
  const [attempt, setAttempt] = useState(0)

  // The link may be renewed while the file is held, and the effect must not start again for it: it asks by id, with the latest source.
  const latest = useRef(source)
  useEffect(() => {
    latest.current = source
  })

  const id = source?.id
  useEffect(() => {
    const file = latest.current
    if (!file || !enabled) return

    let current = true
    setState({ status: 'loading' })
    fileObjects.hold(file).then(
      (url) => {
        if (current) setState(url === null ? { status: 'idle' } : { status: 'ready', url })
      },
      () => {
        if (current) setState({ status: 'failed' })
      },
    )

    return () => {
      current = false
      fileObjects.release(file)
    }
  }, [id, enabled, attempt])

  const retry = useCallback(() => setAttempt((n) => n + 1), [])
  return [state, retry]
}
