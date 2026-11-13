import { type ReactNode, useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useIdentityChanges } from '../../encryption/identityChanges'
import { useKeyStatusStore } from '../../encryption/keyStatus'
import { useNotificationStore } from '../../notifications/store/notificationStore'
import { useLocalSearchSync } from '../../search/hooks/useLocalSearchSync'
import { usePresenceStore } from '../../presence/store/presenceStore'
import { useProfileSync } from '../../profile/hooks/useProfileSync'
import { usePushSync } from '../../push/hooks/usePushSync'
import { useServiceWorkerNavigation } from '../../push/hooks/useServiceWorkerNavigation'
import { ChatConnectionContext } from '../hooks/chatConnectionContext'
import { useSignalR } from '../hooks/useSignalR'
import { useChatStore } from '../store/chatStore'

// Everything below this component can use the live connection. It exists for as long as the signed-in
// user is in the chat: the connection opens when it mounts and closes when it unmounts.
export function ChatConnectionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const connection = useSignalR()
  useProfileSync()
  usePushSync()
  useServiceWorkerNavigation()
  useLocalSearchSync()

  // Leaving the chat, by signing out or because the session expired, must not leave one person's
  // conversations in memory for the next person to sign in on this browser. The stores and the
  // query cache are all emptied when the chat unmounts.
  useEffect(
    () => () => {
      useChatStore.getState().reset()
      useKeyStatusStore.getState().reset()
      useIdentityChanges.getState().reset()
      useNotificationStore.getState().reset()
      usePresenceStore.getState().reset()
      queryClient.clear()
    },
    [queryClient],
  )

  return <ChatConnectionContext value={connection}>{children}</ChatConnectionContext>
}
