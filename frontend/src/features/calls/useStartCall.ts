import { useChatConnection } from '../chat/hooks/chatConnectionContext'
import { useChatStore } from '../chat/store/chatStore'
import type { CallMedia, Conversation } from '../chat/types'
import { useCallStore } from './callStore'

// Whether a call can be made in a conversation now, and the way to make it. Only a direct conversation that was not left can be called, only
// while the connection is up, and only while this browser has no call of its own (a call that just ended is still on the screen for a moment).
export function useStartCall(conversation: Conversation): { canCall: boolean; call: (media: CallMedia) => void } {
  const { calls } = useChatConnection()
  const connected = useChatStore((state) => state.connection === 'connected')
  const idle = useCallStore((state) => state.phase === 'idle')
  const peerId = conversation.type === 'Direct' ? conversation.otherUserId : null
  const canCall = peerId !== null && conversation.leftAt === null && connected && idle

  return {
    canCall,
    call: (media) => {
      if (peerId === null) return
      void calls.start({ conversationId: conversation.id, peerId, peerName: conversation.name, media })
    },
  }
}
