import { useEffect } from 'react'
import { useChatStore } from '../../chat/store/chatStore'

// A conversation is made ready as a member opens it: its newest key period exists, was made for the people in it and is held in this
// browser. That means making the first one for a conversation from before keys, replacing one after someone left, and opening the
// envelope of one a browser has not yet read. Several members may do this together; the server stores one and the others find it.
// Nothing is shown: the conversation works as it did, and a failure is tried again the next time it is opened.
export function useConversationKeys(conversationId: string) {
  const active = useChatStore((state) => state.conversations[conversationId]?.leftAt === null)
  const keyEpoch = useChatStore((state) => state.conversations[conversationId]?.keyEpoch ?? 0)
  const keyRotationRequired = useChatStore((state) => state.conversations[conversationId]?.keyRotationRequired ?? false)

  useEffect(() => {
    if (!active) return

    void import('../conversationKeys').then((keys) => keys.readyConversation(conversationId, { keyEpoch, keyRotationRequired }))
  }, [active, conversationId, keyEpoch, keyRotationRequired])
}
