import { cleanFileName } from '../chat/files/fileNames'
import type { Message } from '../chat/types'
import { wordsOf } from './query/normalize'
import type { LocalStore, StoredMessage } from './store/types'

// What a message becomes in the local store, and the queue that writes the messages the app opens as it opens them.

// The message as the store keeps it, or null when there is nothing to keep: one the server has not confirmed (it has no change number
// yet), or one that could not be opened and is not held sealed either.
export function storedFrom(userId: string, message: Message): StoredMessage | null {
  if (message.changeSeq === undefined) return null

  const base = {
    userId,
    conversationId: message.conversationId,
    id: message.id,
    senderId: message.senderId,
    senderName: message.senderDisplayName,
    createdAt: message.createdAt,
    at: Date.parse(message.createdAt),
    editCount: message.editCount,
    changeSeq: message.changeSeq,
  }

  if (message.isDeleted) return { ...base, state: 'deleted' }

  if (message.content !== null) {
    // The names of the files are written by their sender and sealed in the message; only those the message vouches for are known.
    const files = message.attachments.flatMap((attachment) => {
      if (attachment.file === null) return []
      const name = cleanFileName(attachment.file.name)
      return [{ name, words: wordsOf(name) }]
    })

    return { ...base, state: 'text', text: message.content, words: wordsOf(message.content), files }
  }

  if (message.ciphertext && message.keyEpoch !== undefined && message.clientMessageId !== null) {
    const files = message.attachments.map(({ id, sizeBytes }) => ({ id, sizeBytes }))
    return {
      ...base,
      state: 'sealed',
      sealed: { ciphertext: message.ciphertext, keyEpoch: message.keyEpoch, clientMessageId: message.clientMessageId, files },
    }
  }

  return null
}

// How long the messages that are opened are gathered before they are written, so a page of history is one write and not fifty.
const GATHER_MS = 50

export interface Ingest {
  // Takes a message that was opened. It is written soon, with the others that arrive meanwhile.
  remember: (message: Message) => void
  // Writes what has been gathered now.
  flush: () => Promise<void>
}

interface IngestDeps {
  store: LocalStore
  // Whoever is signed in; nothing is kept when nobody is.
  userId: () => string | undefined
  // A write that failed. What was gathered is lost, which costs nothing lasting: the next read of that conversation brings it again.
  onError: (error: unknown) => void
  schedule?: (work: () => void) => void
}

export function createIngest({ store, userId, onError, schedule = (work) => void setTimeout(work, GATHER_MS) }: IngestDeps): Ingest {
  let gathered: StoredMessage[] = []
  let waiting = false
  let writing: Promise<void> = Promise.resolve()

  const flush = (): Promise<void> => {
    const batch = gathered
    gathered = []
    waiting = false
    if (batch.length === 0) return writing

    const byConversation = new Map<string, StoredMessage[]>()
    // The store keeps the newest copy of a message that is in a write more than once.
    for (const message of batch) {
      const list = byConversation.get(message.conversationId) ?? []
      list.push(message)
      byConversation.set(message.conversationId, list)
    }

    // One write after another, so a later page never lands before an earlier one that is still being written.
    writing = writing.then(async () => {
      for (const [conversationId, messages] of byConversation) {
        try {
          await store.apply(messages[0].userId, conversationId, messages)
        } catch (error) {
          onError(error)
        }
      }
    })

    return writing
  }

  return {
    remember: (message) => {
      const id = userId()
      if (id === undefined) return

      const stored = storedFrom(id, message)
      if (stored === null) return

      gathered.push(stored)
      if (!waiting) {
        waiting = true
        schedule(() => void flush())
      }
    },
    flush,
  }
}
