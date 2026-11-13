import type { Message, MessageWire } from '../../chat/types'
import { storedFrom } from '../ingest'
import type { LocalStore, StoredMessage, StoredSealed } from '../store/types'

// The messages the store holds sealed are opened again when the keys of their conversation may have arrived (the conversation was made
// ready, or an epoch was announced). What opens replaces the sealed copy, which carries the same change number.

function wireOf(message: StoredSealed): MessageWire {
  return {
    id: message.id,
    conversationId: message.conversationId,
    senderId: message.senderId,
    senderDisplayName: message.senderName,
    ciphertext: message.sealed.ciphertext,
    keyEpoch: message.sealed.keyEpoch,
    createdAt: message.createdAt,
    // Only the sizes are kept: they are what ties the files to the names inside the sealed message. The links are of no use here.
    attachments: message.sealed.files.map((file) => ({ id: file.id, sizeBytes: file.sizeBytes, url: '' })),
    clientMessageId: message.sealed.clientMessageId,
    editedAt: null,
    editCount: message.editCount,
    isDeleted: false,
    changeSeq: message.changeSeq,
  }
}

export async function reopenStored(
  store: LocalStore,
  userId: string,
  conversationId: string,
  open: (wires: MessageWire[]) => Promise<Message[]>,
): Promise<void> {
  const sealed = await store.sealed(userId, conversationId)
  if (sealed.length === 0) return

  const opened = await open(sealed.map(wireOf))
  const readable: StoredMessage[] = opened.flatMap((message) => {
    const stored = storedFrom(userId, message)
    return stored?.state === 'text' ? [stored] : []
  })

  if (readable.length > 0) await store.apply(userId, conversationId, readable)
}
