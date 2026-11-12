import { META, MESSAGES, MESSAGES_BY_TIME, READ_POINTS, asPromise, startingWith, transaction } from '../../encryption/idb'
import { mergeReadPoint, newestOfEach, supersedes } from './rules'
import type { LocalStore, ReadPoint, ScanPosition, StoredMessage, StoredSealed } from './types'

// The local store over IndexedDB (see features/encryption/idb.ts for the database it lives in and what that means).

// The shape of what is kept. A store written in another shape is emptied and read again from the server, which is always possible: it
// holds nothing the server does not.
export const STORE_FORMAT = 1

const formatKey = (userId: string) => `searchFormat:${userId}`

// Calls `visit` for each record the cursor reaches until it says stop. True when the cursor ran out.
function walk(request: IDBRequest<IDBCursorWithValue | null>, visit: (value: StoredMessage) => boolean): Promise<boolean> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => {
      const cursor = request.result
      if (!cursor) {
        resolve(true)
        return
      }

      if (visit(cursor.value as StoredMessage)) cursor.continue()
      else resolve(false)
    }
    request.onerror = () => reject(request.error ?? new Error('IndexedDB request failed'))
  })
}

async function forget(tx: IDBTransaction, ...prefix: string[]): Promise<void> {
  await Promise.all([asPromise(tx.objectStore(MESSAGES).delete(startingWith(...prefix))), asPromise(tx.objectStore(READ_POINTS).delete(startingWith(...prefix)))])
}

export const indexedDbLocalStore: LocalStore = {
  prepare: (userId) =>
    transaction([META, MESSAGES, READ_POINTS], 'readwrite', async (tx) => {
      const meta = tx.objectStore(META)
      if ((await asPromise(meta.get(formatKey(userId)))) === STORE_FORMAT) return

      await forget(tx, userId)
      await asPromise(meta.put(STORE_FORMAT, formatKey(userId)))
    }),

  apply: (userId, conversationId, messages, change) =>
    transaction([MESSAGES, READ_POINTS], 'readwrite', async (tx) => {
      const store = tx.objectStore(MESSAGES)

      await Promise.all(
        newestOfEach(messages).map(async (incoming) => {
          const held = (await asPromise(store.get([incoming.userId, incoming.conversationId, incoming.id]))) as StoredMessage | undefined
          if (supersedes(incoming, held)) await asPromise(store.put(incoming))
        }),
      )

      if (change) {
        const points = tx.objectStore(READ_POINTS)
        const held = (await asPromise(points.get([userId, conversationId]))) as ReadPoint | undefined
        await asPromise(points.put(mergeReadPoint(held, userId, conversationId, change)))
      }
    }),

  readPoint: (userId, conversationId) =>
    transaction([READ_POINTS], 'readonly', async (tx) => {
      const found = (await asPromise(tx.objectStore(READ_POINTS).get([userId, conversationId]))) as ReadPoint | undefined
      return found ?? null
    }),

  readPoints: (userId) =>
    transaction([READ_POINTS], 'readonly', async (tx) => (await asPromise(tx.objectStore(READ_POINTS).getAll(startingWith(userId)))) as ReadPoint[]),

  sealed: (userId, conversationId) =>
    transaction([MESSAGES], 'readonly', async (tx) => {
      const records = (await asPromise(tx.objectStore(MESSAGES).getAll(startingWith(userId, conversationId)))) as StoredMessage[]
      return records.filter((record): record is StoredSealed => record.state === 'sealed')
    }),

  scan: (userId, after, visit) =>
    transaction([MESSAGES], 'readonly', async (tx) => {
      const lowest = [userId, -Infinity, '']
      const range = after
        ? IDBKeyRange.bound(lowest, [userId, after.at, after.id], false, true)
        : IDBKeyRange.bound(lowest, [userId, Infinity, '￿'])

      let last: ScanPosition | null = null
      const exhausted = await walk(tx.objectStore(MESSAGES).index(MESSAGES_BY_TIME).openCursor(range, 'prev'), (message) => {
        last = { at: message.at, id: message.id }
        return visit(message)
      })

      return { last, exhausted }
    }),

  forgetConversation: (userId, conversationId) => transaction([MESSAGES, READ_POINTS], 'readwrite', (tx) => forget(tx, userId, conversationId)),

  forgetAll: (userId) =>
    transaction([META, MESSAGES, READ_POINTS], 'readwrite', async (tx) => {
      await forget(tx, userId)
      await asPromise(tx.objectStore(META).delete(formatKey(userId)))
    }),
}
