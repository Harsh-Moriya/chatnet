import { useCallback, useEffect, useMemo, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { createChatConnection } from '../../../lib/signalr'
import { catchUpAll } from '../api/catchUp'
import { isTokenExpired } from '../../../lib/jwt'
import { useAuthStore } from '../../auth/store/authStore'
import { useChatStore } from '../store/chatStore'
import { connectChat, type ChatConnection } from './chatConnection'

// Owns the live connection for as long as the component that calls it is mounted: it connects on
// mount and disconnects on unmount. All the logic is in connectChat; this is only the React side.
//
// React runs an effect, undoes it and runs it again in development. That is why the connection
// is created inside the effect and disposed in its cleanup: the abandoned first connection
// shuts down quietly and the second one is the real one.
export function useSignalR() {
  const queryClient = useQueryClient()
  const connectionRef = useRef<ChatConnection | null>(null)

  useEffect(() => {
    useChatStore.getState().setConnection('connecting')

    let connection: ChatConnection | null = null

    // The connection is opened one tick later. React's development double-mount runs this effect,
    // undoes it and runs it again within the same tick, so the first run is cancelled before it
    // opens anything; otherwise an abandoned connection logs a "stopped during negotiation" error
    // on every page load.
    const opening = setTimeout(() => {
      connection = connectChat(createChatConnection(), {
        getUserId: () => useAuthStore.getState().user?.userId ?? null,

        isSessionExpired: () => isTokenExpired(useAuthStore.getState().token),

        // Clearing the session sends the user to the sign-in page, which unmounts the chat and closes
        // this connection.
        onSessionExpired: () => useAuthStore.getState().clearAuth(),

        // Events sent while the connection was down were lost, so REST puts the state back in
        // charge. The list is reloaded; every conversation that was loaded asks the server for the
        // messages after its newest one (a conversation too far behind is reset and loads afresh).
        // This is a connection event, not a message event, so it does not break the rule against
        // reloading on pushes.
        onReconnected: () => {
          void queryClient.invalidateQueries({ queryKey: ['conversations'] })
          void catchUpAll({
            onTooFarBehind: (conversationId) =>
              void queryClient.resetQueries({ queryKey: ['messages', conversationId] }),
          })
        },
      })

      connectionRef.current = connection
    }, 0)

    return () => {
      clearTimeout(opening)
      connection?.dispose()
      connectionRef.current = null
    }
  }, [queryClient])

  const sendTyping = useCallback((conversationId: string, isTyping: boolean) => {
    connectionRef.current?.sendTyping(conversationId, isTyping)
  }, [])

  return useMemo(() => ({ sendTyping }), [sendTyping])
}
