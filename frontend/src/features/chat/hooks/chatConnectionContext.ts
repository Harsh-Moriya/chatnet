import { createContext, useContext } from 'react'

// What components below the connection provider can do with the live connection. Today that is
// only reporting typing; everything else arrives through the store.
export interface ChatConnectionApi {
  sendTyping: (conversationId: string, isTyping: boolean) => void
}

export const ChatConnectionContext = createContext<ChatConnectionApi | null>(null)

export function useChatConnection(): ChatConnectionApi {
  const connection = useContext(ChatConnectionContext)
  if (!connection) throw new Error('useChatConnection must be used inside ChatConnectionProvider.')

  return connection
}
