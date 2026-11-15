import { createContext, useContext } from 'react'
import type { CallController } from '../../calls/callController'

// What the person does with a call: the rest of a call (what it is, and its media) is in the call store.
export type CallActions = Pick<CallController, 'start' | 'accept' | 'end' | 'setMuted' | 'setCameraOff' | 'switchDevice'>

// What components below the connection provider can do with the live connection: report typing, and make and answer calls. Everything
// else arrives through the store.
export interface ChatConnectionApi {
  sendTyping: (conversationId: string, isTyping: boolean) => void
  calls: CallActions
}

export const ChatConnectionContext = createContext<ChatConnectionApi | null>(null)

export function useChatConnection(): ChatConnectionApi {
  const connection = useContext(ChatConnectionContext)
  if (!connection) throw new Error('useChatConnection must be used inside ChatConnectionProvider.')

  return connection
}
