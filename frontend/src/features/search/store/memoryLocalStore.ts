import { mergeReadPoint, newestOfEach, supersedes } from './rules'
import type { LocalStore, ReadPoint, ScanPosition, StoredMessage, StoredSealed } from './types'

// The local store in memory, for tests that are about what is done with a store and not about IndexedDB. It follows the same rules
// (rules.ts), and keeps the same shape of keys, so what holds here holds of the real one; the real one has its own tests.
export class MemoryLocalStore implements LocalStore {
  readonly messages = new Map<string, StoredMessage>()
  readonly points = new Map<string, ReadPoint>()
  formats = new Map<string, number>()
  prepared: string[] = []

  private static key = (userId: string, conversationId: string, id: string) => `${userId}|${conversationId}|${id}`
  private static pointKey = (userId: string, conversationId: string) => `${userId}|${conversationId}`

  prepare = (userId: string): Promise<void> => {
    this.prepared.push(userId)
    return Promise.resolve()
  }

  apply = (userId: string, conversationId: string, messages: StoredMessage[], change?: Parameters<LocalStore['apply']>[3]): Promise<void> => {
    for (const incoming of newestOfEach(messages)) {
      const key = MemoryLocalStore.key(incoming.userId, incoming.conversationId, incoming.id)
      if (supersedes(incoming, this.messages.get(key))) this.messages.set(key, incoming)
    }

    if (change) {
      const key = MemoryLocalStore.pointKey(userId, conversationId)
      this.points.set(key, mergeReadPoint(this.points.get(key), userId, conversationId, change))
    }

    return Promise.resolve()
  }

  readPoint = (userId: string, conversationId: string): Promise<ReadPoint | null> =>
    Promise.resolve(this.points.get(MemoryLocalStore.pointKey(userId, conversationId)) ?? null)

  readPoints = (userId: string): Promise<ReadPoint[]> => Promise.resolve([...this.points.values()].filter((point) => point.userId === userId))

  sealed = (userId: string, conversationId: string): Promise<StoredSealed[]> =>
    Promise.resolve(
      [...this.messages.values()].filter(
        (message): message is StoredSealed => message.userId === userId && message.conversationId === conversationId && message.state === 'sealed',
      ),
    )

  scan = (
    userId: string,
    after: ScanPosition | null,
    visit: (message: StoredMessage) => boolean,
  ): Promise<{ last: ScanPosition | null; exhausted: boolean }> => {
    const newestFirst = [...this.messages.values()]
      .filter((message) => message.userId === userId)
      .sort((a, b) => b.at - a.at || (a.id < b.id ? 1 : a.id > b.id ? -1 : 0))
      .filter((message) => after === null || message.at < after.at || (message.at === after.at && message.id < after.id))

    let last: ScanPosition | null = null
    for (const message of newestFirst) {
      last = { at: message.at, id: message.id }
      if (!visit(message)) return Promise.resolve({ last, exhausted: false })
    }

    return Promise.resolve({ last, exhausted: true })
  }

  forgetConversation = (userId: string, conversationId: string): Promise<void> => {
    for (const [key, message] of this.messages) if (message.userId === userId && message.conversationId === conversationId) this.messages.delete(key)
    this.points.delete(MemoryLocalStore.pointKey(userId, conversationId))
    return Promise.resolve()
  }

  forgetAll = (userId: string): Promise<void> => {
    for (const [key, message] of this.messages) if (message.userId === userId) this.messages.delete(key)
    for (const [key, point] of this.points) if (point.userId === userId) this.points.delete(key)
    this.formats.delete(userId)
    return Promise.resolve()
  }
}
