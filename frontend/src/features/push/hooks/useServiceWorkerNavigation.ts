import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { conversationPath, focusState } from '../../notifications/navigation'
import { parseOpenConversation } from '../swMessages'

// A click on an alert while the application is already open: the service worker brings the window forward and sends this page a
// message, and the page moves to the conversation (and for a mention, to the message) as it does for a click on an in-page alert.
export function useServiceWorkerNavigation() {
  const navigate = useNavigate()

  useEffect(() => {
    if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return

    const onMessage = (event: MessageEvent<unknown>) => {
      const request = parseOpenConversation(event.data)
      if (request) navigate(conversationPath(request.conversationId), { state: focusState(request.messageId) })
    }

    navigator.serviceWorker.addEventListener('message', onMessage)
    return () => navigator.serviceWorker.removeEventListener('message', onMessage)
  }, [navigate])
}
