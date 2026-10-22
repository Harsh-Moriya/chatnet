import axios from 'axios'
import { useEffect, useRef, useState } from 'react'
import { getApiError } from '../../../lib/errors'
import { newClientMessageId as newUploadId } from '../../../lib/uuid'
import { UploadFailed, uploadFile } from '../api/uploadsApi'
import {
  type UploadItem,
  addFiles,
  markFailed,
  markReady,
  removeItem,
  setProgress,
} from '../uploads'

// The files being attached to the message that is being written. Choosing a file starts its upload at once,
// so by the time the person has finished typing it is usually done. The rules for the list live in uploads.ts;
// this hook only runs the uploads and keeps the list in React state.
export function useAttachmentUploads(conversationId: string) {
  const [items, setItems] = useState<UploadItem[]>([])
  const [rejected, setRejected] = useState<string[]>([])

  // The list is read and changed from event handlers and from upload callbacks that finish later. The ref is the
  // one copy they all agree on; the state only exists to draw it.
  const current = useRef<UploadItem[]>([])
  const running = useRef(new Map<string, AbortController>())

  function apply(change: (list: UploadItem[]) => UploadItem[]) {
    current.current = change(current.current)
    setItems(current.current)
  }

  // Leaving the conversation ends the uploads that are still going; their files would never be attached.
  useEffect(() => {
    const uploads = running.current
    return () => uploads.forEach((controller) => controller.abort())
  }, [])

  function start(item: UploadItem) {
    const controller = new AbortController()
    running.current.set(item.id, controller)

    uploadFile(conversationId, item.file, (fraction) => apply((list) => setProgress(list, item.id, fraction)), controller.signal)
      .then((uploadKey) => apply((list) => markReady(list, item.id, uploadKey)))
      .catch((error: unknown) => {
        // A removed upload is cancelled on purpose and is already gone from the list.
        if (axios.isCancel(error)) return
        apply((list) => markFailed(list, item.id, error instanceof UploadFailed ? error.message : getApiError(error)))
      })
      .finally(() => running.current.delete(item.id))
  }

  function add(files: File[]) {
    const result = addFiles(current.current, files, newUploadId)

    setRejected(result.rejected)
    if (result.added.length === 0) return

    apply(() => result.items)
    result.added.forEach(start)
  }

  function remove(id: string) {
    running.current.get(id)?.abort()
    apply((list) => removeItem(list, id))
  }

  // After the message is sent: the files belong to it now.
  function clear() {
    setRejected([])
    apply(() => [])
  }

  return { items, rejected, add, remove, clear }
}
