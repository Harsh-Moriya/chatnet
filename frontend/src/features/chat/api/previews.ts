import { useChatStore } from '../store/chatStore'
import type { Conversation } from '../types'
import { openLastMessage } from './openWire'

// How many previews are opened at once. Opening one may fetch the keys of its conversation, and a list of many conversations would
// otherwise ask for all of them together.
const PREVIEW_CONCURRENCY = 3

// The previews of the conversations that are still being opened, a few at a time. Each takes its place in the list when it is done, and a
// conversation whose key is not here shows why instead of a text.
export async function openPreviews(conversations: Conversation[]): Promise<void> {
  const waiting = conversations.filter((c) => c.lastMessage?.unreadable === 'pending')
  let next = 0

  const worker = async () => {
    while (next < waiting.length) {
      const { id, lastMessage } = waiting[next++]
      useChatStore.getState().applyOpenedPreview(id, await openLastMessage(id, lastMessage!))
    }
  }

  await Promise.all(Array.from({ length: Math.min(PREVIEW_CONCURRENCY, waiting.length) }, worker))
}
