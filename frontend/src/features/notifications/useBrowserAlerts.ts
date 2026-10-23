import { useEffect, useMemo, useRef } from 'react'
import { useMatch, useNavigate } from 'react-router-dom'
import { useAuthStore } from '../auth/store/authStore'
import { useChatStore } from '../chat/store/chatStore'
import type { Message } from '../chat/types'
import {
  messageAlertText,
  notificationAlertText,
  shouldAlertForMessage,
  shouldAlertForNotification,
} from './alerts'
import { currentView, showAlert } from './browserAlert'
import { conversationPath, focusState } from './navigation'
import type { NotificationItem } from './types'

// Raises the browser's alerts for what arrives over the connection. Both functions are stable and read the
// open conversation and the router when they run, so the connection, which is made once, always uses the
// current ones.
export function useBrowserAlerts() {
  const navigate = useNavigate()
  const open = useMatch('/chat/:conversationId')?.params.conversationId ?? null

  const latest = useRef({ navigate, open })
  useEffect(() => {
    latest.current = { navigate, open }
  })

  return useMemo(() => {
    const openConversation = (conversationId: string, messageId: string | null) =>
      latest.current.navigate(conversationPath(conversationId), { state: focusState(messageId) })

    return {
      message(message: Message) {
        const conversation = useChatStore.getState().conversations[message.conversationId]
        const selfId = useAuthStore.getState().user?.userId ?? null

        if (!shouldAlertForMessage(message, selfId, conversation, currentView(latest.current.open))) return

        showAlert(messageAlertText(message, conversation), () => openConversation(message.conversationId, null))
      },

      notification(notification: NotificationItem) {
        if (!shouldAlertForNotification(notification, currentView(latest.current.open))) return

        const { conversationId, messageId } = notification
        const name = conversationId ? useChatStore.getState().conversations[conversationId]?.name : undefined

        showAlert(notificationAlertText(notification, name), () => {
          if (conversationId) openConversation(conversationId, messageId)
        })
      },
    }
  }, [])
}
