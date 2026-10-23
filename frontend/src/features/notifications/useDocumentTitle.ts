import { useEffect } from 'react'
import { useChatStore } from '../chat/store/chatStore'
import { APP_TITLE, documentTitle, totalUnread } from './title'

// Keeps the tab title in step with the unread messages, and puts the plain title back when the chat goes away. The
// selector returns a number, so the title is only rewritten when the number changes.
export function useDocumentTitle(): void {
  const unread = useChatStore((state) => totalUnread(state.conversations))

  useEffect(() => {
    document.title = documentTitle(unread)
  }, [unread])

  useEffect(
    () => () => {
      document.title = APP_TITLE
    },
    [],
  )
}
