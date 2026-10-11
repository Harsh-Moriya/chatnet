import { useEffect, useMemo } from 'react'
import { useChatConnection } from './chatConnectionContext'
import { createTypingController } from './typingController'

// Reports the signed-in user's typing in one conversation. Call `input(text)` from the composer's
// change handler and `stop()` when a message is sent. Switching conversation or leaving the page
// ends typing in the old one.
export function useTypingIndicator(conversationId: string) {
  const { sendTyping } = useChatConnection()

  const controller = useMemo(
    () => createTypingController((isTyping) => sendTyping(conversationId, isTyping)),
    [conversationId, sendTyping],
  )

  useEffect(() => () => controller.dispose(), [controller])

  return controller
}
