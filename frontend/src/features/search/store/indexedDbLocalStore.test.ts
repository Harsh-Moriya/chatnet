import { IDBFactory, IDBKeyRange } from 'fake-indexeddb'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { DATABASE, META, transaction, asPromise } from '../../encryption/idb'
import { ANA, BEN, deletedStored, sealedStored, stored } from '../testKit'
import { STORE_FORMAT, indexedDbLocalStore as store } from './indexedDbLocalStore'
import type { StoredMessage } from './types'

// A fresh database for every test, as a new browser profile would be.
beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
  globalThis.IDBKeyRange = IDBKeyRange
})

// Every message the scan reaches, newest first.
async function all(userId = ANA): Promise<StoredMessage[]> {
  const seen: StoredMessage[] = []
  await store.scan(userId, null, (message) => {
    seen.push(message)
    return true
  })
  return seen
}

const at = (seconds: number) => new Date(Date.parse('2026-11-12T10:00:00Z') + seconds * 1000).toISOString()

describe('keeping messages', () => {
  it('keeps a message and reads it back whole', async () => {
    const message = stored({ id: 'm1', text: 'Hello Wörld', files: [{ name: 'plan.pdf', words: ['plan', 'pdf'] }] })

    await store.apply(ANA, 'c1', [message])

    expect(await all()).toEqual([message])
  })

  it('lists the messages newest first, whatever order they were written in', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'b', createdAt: at(20) }), stored({ id: 'a', createdAt: at(10) }), stored({ id: 'c', createdAt: at(30) })])

    expect((await all()).map((m) => m.id)).toEqual(['c', 'b', 'a'])
  })

  it('lists the messages of all conversations together by time', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a', conversationId: 'c1', createdAt: at(10) }), stored({ id: 'c', conversationId: 'c1', createdAt: at(30) })])
    await store.apply(ANA, 'c2', [stored({ id: 'b', conversationId: 'c2', createdAt: at(20) })])

    expect((await all()).map((m) => m.id)).toEqual(['c', 'b', 'a'])
  })

  it('tells messages from one moment apart by their id, the same way every time', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'b', createdAt: at(5) }), stored({ id: 'a', createdAt: at(5) }), stored({ id: 'c', createdAt: at(5) })])

    expect((await all()).map((m) => m.id)).toEqual(['c', 'b', 'a'])
  })

  it('keeps the same message under its conversation, so two conversations may share an id', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'same', conversationId: 'c1', text: 'in one' })])
    await store.apply(ANA, 'c2', [stored({ id: 'same', conversationId: 'c2', text: 'in two' })])

    expect(await all()).toHaveLength(2)
  })

  it('takes an edit over the message it edits', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'm1', text: 'before', changeSeq: 1 })])

    await store.apply(ANA, 'c1', [stored({ id: 'm1', text: 'after', changeSeq: 2, editCount: 1 })])

    const [only] = await all()
    expect(only).toMatchObject({ text: 'after', changeSeq: 2 })
  })

  it('does not let an older copy take the place of a newer one', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'm1', text: 'after', changeSeq: 2 })])

    await store.apply(ANA, 'c1', [stored({ id: 'm1', text: 'before', changeSeq: 1 })])

    const [only] = await all()
    expect(only).toMatchObject({ text: 'after', changeSeq: 2 })
  })

  it('does not let a late copy bring back a message that was deleted', async () => {
    await store.apply(ANA, 'c1', [deletedStored({ id: 'm1', changeSeq: 3 })])

    await store.apply(ANA, 'c1', [stored({ id: 'm1', text: 'back from the dead', changeSeq: 2 })])

    const [only] = await all()
    expect(only.state).toBe('deleted')
  })

  it('keeps the newest copy of a message that is twice in one write', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'm1', text: 'newer', changeSeq: 5 }), stored({ id: 'm1', text: 'older', changeSeq: 2 })])

    const [only] = await all()
    expect(only).toMatchObject({ text: 'newer' })
  })

  it('replaces a message that could not be opened by the same message once it is opened', async () => {
    await store.apply(ANA, 'c1', [sealedStored({ id: 'm1', changeSeq: 4 })])

    await store.apply(ANA, 'c1', [stored({ id: 'm1', changeSeq: 4, text: 'now readable' })])

    const [only] = await all()
    expect(only).toMatchObject({ state: 'text', text: 'now readable' })
  })

  it('writes nothing when it is given nothing', async () => {
    await store.apply(ANA, 'c1', [])

    expect(await all()).toEqual([])
  })

  it('keeps a long conversation in order', async () => {
    const messages = Array.from({ length: 300 }, (_, i) => stored({ id: `m${String(i).padStart(4, '0')}`, createdAt: at(i), changeSeq: i + 1 }))
    await store.apply(ANA, 'c1', messages)

    const seen = await all()

    expect(seen).toHaveLength(300)
    expect(seen.map((m) => m.id)).toEqual(messages.map((m) => m.id).reverse())
  })
})

describe('reading the messages', () => {
  it('stops when told to and says where, then goes on from there', async () => {
    await store.apply(ANA, 'c1', ['a', 'b', 'c', 'd', 'e'].map((id, i) => stored({ id, createdAt: at(i) })))

    const first: string[] = []
    const one = await store.scan(ANA, null, (message) => {
      first.push(message.id)
      return first.length < 2
    })
    const rest: string[] = []
    const two = await store.scan(ANA, one.last, (message) => {
      rest.push(message.id)
      return true
    })

    expect(first).toEqual(['e', 'd'])
    expect(one).toEqual({ last: { at: Date.parse(at(3)), id: 'd' }, exhausted: false })
    expect(rest).toEqual(['c', 'b', 'a'])
    expect(two.exhausted).toBe(true)
  })

  it('says it ran out when it reached the oldest message', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a' })])

    const result = await store.scan(ANA, null, () => true)

    expect(result.exhausted).toBe(true)
    expect(result.last?.id).toBe('a')
  })

  it('says it ran out of an empty store', async () => {
    expect(await store.scan(ANA, null, () => true)).toEqual({ last: null, exhausted: true })
  })

  it('does not visit a message twice across two scans, even at one moment', async () => {
    await store.apply(ANA, 'c1', ['a', 'b', 'c', 'd'].map((id) => stored({ id, createdAt: at(5) })))

    const seen: string[] = []
    let from = null as Awaited<ReturnType<typeof store.scan>>['last']
    for (;;) {
      const result = await store.scan(ANA, from, (message) => {
        seen.push(message.id)
        return false
      })
      if (result.exhausted || !result.last) break
      from = result.last
    }

    expect(seen).toEqual(['d', 'c', 'b', 'a'])
  })

  it('goes through the deleted and the sealed too, for the search to leave out', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a', createdAt: at(1) }), deletedStored({ id: 'b', createdAt: at(2) }), sealedStored({ id: 'c', createdAt: at(3) })])

    expect((await all()).map((m) => m.state)).toEqual(['sealed', 'deleted', 'text'])
  })
})

describe('what is kept for whom', () => {
  it('keeps what one person read from another on the same browser', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'mine', userId: ANA })])
    await store.apply(BEN, 'c1', [stored({ id: 'theirs', userId: BEN })])

    expect((await all(ANA)).map((m) => m.id)).toEqual(['mine'])
    expect((await all(BEN)).map((m) => m.id)).toEqual(['theirs'])
  })

  it('keeps the read points of one person from another', async () => {
    await store.apply(ANA, 'c1', [], { cursor: 5 })
    await store.apply(BEN, 'c1', [], { cursor: 9 })

    expect((await store.readPoints(ANA)).map((p) => p.cursor)).toEqual([5])
    expect((await store.readPoint(BEN, 'c1'))?.cursor).toBe(9)
  })

  it('forgets one conversation and what was read of it, and nothing else', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a', conversationId: 'c1' })], { cursor: 3, historyRead: true })
    await store.apply(ANA, 'c2', [stored({ id: 'b', conversationId: 'c2' })], { cursor: 4, historyRead: true })

    await store.forgetConversation(ANA, 'c1')

    expect((await all()).map((m) => m.id)).toEqual(['b'])
    expect(await store.readPoint(ANA, 'c1')).toBeNull()
    expect((await store.readPoint(ANA, 'c2'))?.cursor).toBe(4)
  })

  it('forgets everything of one person and leaves another their own', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a', userId: ANA })], { cursor: 3 })
    await store.apply(BEN, 'c1', [stored({ id: 'b', userId: BEN })], { cursor: 4 })

    await store.forgetAll(ANA)

    expect(await all(ANA)).toEqual([])
    expect(await store.readPoints(ANA)).toEqual([])
    expect((await all(BEN)).map((m) => m.id)).toEqual(['b'])
    expect(await store.readPoints(BEN)).toHaveLength(1)
  })

  it('forgets a conversation whose id begins like another', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a', conversationId: 'c1' })])
    await store.apply(ANA, 'c10', [stored({ id: 'b', conversationId: 'c10' })])

    await store.forgetConversation(ANA, 'c1')

    expect((await all()).map((m) => m.id)).toEqual(['b'])
  })
})

describe('how far each conversation has been read', () => {
  it('has no read point for a conversation nothing was read of', async () => {
    expect(await store.readPoint(ANA, 'c1')).toBeNull()
    expect(await store.readPoints(ANA)).toEqual([])
  })

  it('records a page together with the point it brings the conversation to', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a' })], { startSeq: 12, oldestId: 'a', historyRead: false, cursor: null })

    expect(await store.readPoint(ANA, 'c1')).toEqual({
      userId: ANA,
      conversationId: 'c1',
      cursor: null,
      startSeq: 12,
      oldestId: 'a',
      historyRead: false,
    })
  })

  it('moves the cursor forward and never back', async () => {
    await store.apply(ANA, 'c1', [], { cursor: 10 })
    await store.apply(ANA, 'c1', [], { cursor: 15 })
    await store.apply(ANA, 'c1', [], { cursor: 4 })

    expect((await store.readPoint(ANA, 'c1'))?.cursor).toBe(15)
  })

  it('keeps the first starting number, and a history that was read stays read, whatever a slower writer says after', async () => {
    await store.apply(ANA, 'c1', [], { startSeq: 12, oldestId: 'a', historyRead: true, cursor: 12 })
    await store.apply(ANA, 'c1', [], { startSeq: 30, oldestId: 'b', historyRead: false, cursor: null })

    expect(await store.readPoint(ANA, 'c1')).toMatchObject({ startSeq: 12, historyRead: true, cursor: 12, oldestId: 'b' })
  })

  it('moves the point without messages, as when a page of changes holds nothing the person can read', async () => {
    await store.apply(ANA, 'c1', [], { cursor: 7 })

    expect((await store.readPoint(ANA, 'c1'))?.cursor).toBe(7)
  })
})

describe('the messages that could not be opened', () => {
  it('lists those of one conversation', async () => {
    await store.apply(ANA, 'c1', [sealedStored({ id: 'a', conversationId: 'c1' }), stored({ id: 'b', conversationId: 'c1' }), deletedStored({ id: 'd', conversationId: 'c1' })])
    await store.apply(ANA, 'c2', [sealedStored({ id: 'c', conversationId: 'c2' })])

    expect((await store.sealed(ANA, 'c1')).map((m) => m.id)).toEqual(['a'])
  })
})

describe('the shape of what is kept', () => {
  it('empties a store written in another shape, and remembers the one it has now', async () => {
    await store.prepare(ANA)
    await store.apply(ANA, 'c1', [stored({ id: 'a' })], { cursor: 3 })
    await transaction([META], 'readwrite', (tx) => asPromise(tx.objectStore(META).put(STORE_FORMAT + 1, `searchFormat:${ANA}`)))

    await store.prepare(ANA)

    expect(await all()).toEqual([])
    expect(await store.readPoints(ANA)).toEqual([])
    expect(await transaction([META], 'readonly', (tx) => asPromise(tx.objectStore(META).get(`searchFormat:${ANA}`)))).toBe(STORE_FORMAT)
  })

  it('keeps what it has when the shape is the same', async () => {
    await store.prepare(ANA)
    await store.apply(ANA, 'c1', [stored({ id: 'a' })], { cursor: 3 })

    await store.prepare(ANA)

    expect((await all()).map((m) => m.id)).toEqual(['a'])
  })

  it('does not empty the store of another person when it prepares this one', async () => {
    await store.prepare(BEN)
    await store.apply(BEN, 'c1', [stored({ id: 'b', userId: BEN })])

    await store.prepare(ANA)

    expect((await all(BEN)).map((m) => m.id)).toEqual(['b'])
  })

  it('is in the one database the keys are in, so signing out forgets both from one place', async () => {
    await store.apply(ANA, 'c1', [stored({ id: 'a' })])

    const databases = await indexedDB.databases()

    expect(databases.map((d) => d.name)).toEqual([DATABASE])
  })
})

describe('where there is no IndexedDB', () => {
  it('refuses, so the search can say it is not available in this browser', async () => {
    vi.stubGlobal('indexedDB', undefined)

    await expect(store.prepare(ANA)).rejects.toThrow('IndexedDB is not available')
  })
})
