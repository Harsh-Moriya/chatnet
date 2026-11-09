import { EPOCH_KEYS, asPromise, startingWith, transaction } from './idb'
import type { EpochKeyStore } from './types'

// The keys of conversations, kept in this browser (see idb.ts for why, and for what that means). One record per person, conversation
// and epoch, holding the 32 bytes of the key.

interface StoredEpochKey {
  userId: string
  conversationId: string
  epoch: number
  key: Uint8Array
}

export const indexedDbEpochKeyStore: EpochKeyStore = {
  load: (userId, conversationId, epoch) =>
    transaction([EPOCH_KEYS], 'readonly', async (tx) => {
      const found = (await asPromise(tx.objectStore(EPOCH_KEYS).get([userId, conversationId, epoch]))) as StoredEpochKey | undefined
      return found?.key ?? null
    }),

  save: (userId, conversationId, epoch, key) =>
    transaction([EPOCH_KEYS], 'readwrite', async (tx) => {
      const record: StoredEpochKey = { userId, conversationId, epoch, key }
      await asPromise(tx.objectStore(EPOCH_KEYS).put(record))
    }),

  highest: (userId, conversationId) =>
    transaction([EPOCH_KEYS], 'readonly', async (tx) => {
      const cursor = tx.objectStore(EPOCH_KEYS).openCursor(startingWith(userId, conversationId), 'prev')
      const newest = (await asPromise(cursor))?.value as StoredEpochKey | undefined
      return newest?.epoch ?? 0
    }),

  removeAll: (userId) =>
    transaction([EPOCH_KEYS], 'readwrite', async (tx) => {
      await asPromise(tx.objectStore(EPOCH_KEYS).delete(startingWith(userId)))
    }),
}
