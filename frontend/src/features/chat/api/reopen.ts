import { useChatStore } from '../store/chatStore'
import { openLastMessage, openMessage, sealedOf } from './openWire'

// What was held of a conversation that could not be opened is opened again when its keys may have arrived: the conversation was made
// ready, or an epoch was announced. The opener is told to forget that keys were missing, so it asks for them again.
export async function reopenConversation(conversationId: string): Promise<void> {
  try {
    ;(await import('../../encryption/conversationKeys')).messageOpener().forget(conversationId)
  } catch {
    // The keys are not available, so nothing can be opened.
    return
  }

  const chat = () => useChatStore.getState()

  for (const message of (chat().messages[conversationId] ?? []).filter((m) => !m.isDeleted && m.unreadable)) {
    const wire = sealedOf(message)
    if (!wire) continue

    const opened = await openMessage(wire)
    chat().applyOpened(conversationId, message.id, message.editCount, { content: opened.content, unreadable: opened.unreadable ?? null })
  }

  const shown = chat().conversations[conversationId]?.lastMessage
  if (shown?.sealed && shown.unreadable) chat().applyOpenedPreview(conversationId, await openLastMessage(conversationId, shown))
}
