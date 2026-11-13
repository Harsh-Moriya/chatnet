import { IDBFactory, IDBKeyRange } from 'fake-indexeddb'
import { beforeEach, describe, expect, it } from 'vitest'
import { parseQuery } from './query/parse'
import { searchLocal } from './search'
import { SEARCH_PAGE_SIZE } from './searchRules'
import { indexedDbLocalStore } from './store/indexedDbLocalStore'
import { MemoryLocalStore } from './store/memoryLocalStore'
import type { LocalStore, StoredMessage } from './store/types'
import { ANA, BEN, deletedStored, sealedStored, stored } from './testKit'

const at = (seconds: number) => new Date(Date.parse('2026-11-12T10:00:00Z') + seconds * 1000).toISOString()
const query = (text: string) => parseQuery(text)!

// The same searches over the real store and over the one in memory: the search must not depend on which.
const stores: [string, () => LocalStore][] = [
  ['the store in IndexedDB', () => indexedDbLocalStore],
  ['the store in memory', () => new MemoryLocalStore()],
]

beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
  globalThis.IDBKeyRange = IDBKeyRange
})

describe.each(stores)('searching %s', (_name, makeStore) => {
  async function withMessages(...messages: StoredMessage[]): Promise<LocalStore> {
    const store = makeStore()
    await store.apply(ANA, 'c1', messages.filter((m) => m.conversationId === 'c1'))
    await store.apply(ANA, 'c2', messages.filter((m) => m.conversationId === 'c2'))
    return store
  }

  it('finds the messages that match, newest first', async () => {
    const store = await withMessages(
      stored({ id: 'old', text: 'budget one', createdAt: at(1) }),
      stored({ id: 'new', text: 'budget two', createdAt: at(3) }),
      stored({ id: 'mid', text: 'budget three', createdAt: at(2) }),
      stored({ id: 'other', text: 'lunch', createdAt: at(4) }),
    )

    const page = await searchLocal(store, ANA, query('budget'), null)

    expect(page.hits.map((hit) => hit.messageId)).toEqual(['new', 'mid', 'old'])
    expect(page.next).toBeNull()
  })

  it('describes a hit by who said it, where, when, and the text around the match', async () => {
    const store = await withMessages(stored({ id: 'm1', conversationId: 'c1', senderId: BEN, senderName: 'Ben', createdAt: at(5), text: 'see the budget today' }))

    const [hit] = (await searchLocal(store, ANA, query('budget'), null)).hits

    expect(hit).toEqual({
      messageId: 'm1',
      conversationId: 'c1',
      senderId: BEN,
      senderDisplayName: 'Ben',
      createdAt: at(5),
      snippet: [
        { text: 'see the ', match: false },
        { text: 'budget', match: true },
        { text: ' today', match: false },
      ],
      fileName: null,
    })
  })

  it('searches the conversations together', async () => {
    const store = await withMessages(
      stored({ id: 'a', conversationId: 'c1', text: 'needle', createdAt: at(1) }),
      stored({ id: 'b', conversationId: 'c2', text: 'needle', createdAt: at(2) }),
    )

    const page = await searchLocal(store, ANA, query('needle'), null)

    expect(page.hits.map((hit) => [hit.messageId, hit.conversationId])).toEqual([['b', 'c2'], ['a', 'c1']])
  })

  it('finds a message by the name of a file, with no snippet', async () => {
    const store = await withMessages(
      stored({ id: 'm1', text: 'here you go', files: [{ name: 'Quarterly plan.pdf', words: ['quarterly', 'plan', 'pdf'] }] }),
    )

    const [hit] = (await searchLocal(store, ANA, query('quarterly'), null)).hits

    expect(hit).toMatchObject({ messageId: 'm1', fileName: 'Quarterly plan.pdf', snippet: [] })
  })

  it('finds by the text when the text and a file both match, since the text is what was said', async () => {
    const store = await withMessages(stored({ id: 'm1', text: 'the plan is attached', files: [{ name: 'plan.pdf', words: ['plan', 'pdf'] }] }))

    const [hit] = (await searchLocal(store, ANA, query('plan'), null)).hits

    expect(hit.fileName).toBeNull()
    expect(hit.snippet.some((part) => part.match)).toBe(true)
  })

  it('finds the file whose name matches among several', async () => {
    const store = await withMessages(
      stored({ id: 'm1', text: '', files: [{ name: 'a.png', words: ['a', 'png'] }, { name: 'budget.xlsx', words: ['budget', 'xlsx'] }] }),
    )

    const [hit] = (await searchLocal(store, ANA, query('budget'), null)).hits

    expect(hit.fileName).toBe('budget.xlsx')
  })

  it('never finds a deleted message, nor one that could not be opened', async () => {
    const store = await withMessages(
      deletedStored({ id: 'gone', createdAt: at(1) }),
      sealedStored({ id: 'locked', createdAt: at(2) }),
      stored({ id: 'there', text: 'needle', createdAt: at(3) }),
    )

    const page = await searchLocal(store, ANA, query('needle'), null)

    expect(page.hits.map((hit) => hit.messageId)).toEqual(['there'])
  })

  it('finds nothing in a store that is empty, and says it ran out', async () => {
    const store = makeStore()

    expect(await searchLocal(store, ANA, query('needle'), null)).toEqual({ hits: [], next: null })
  })

  it('does not find what another person on this browser read', async () => {
    const store = makeStore()
    await store.apply(BEN, 'c1', [stored({ userId: BEN, id: 'theirs', text: 'needle' })])

    expect((await searchLocal(store, ANA, query('needle'), null)).hits).toEqual([])
    expect((await searchLocal(store, BEN, query('needle'), null)).hits).toHaveLength(1)
  })

  it('follows the syntax: phrases, exclusions and either of two', async () => {
    const store = await withMessages(
      stored({ id: 'a', text: 'the big meeting is on', createdAt: at(1) }),
      stored({ id: 'b', text: 'a meeting, a big one', createdAt: at(2) }),
      stored({ id: 'c', text: 'big meeting draft', createdAt: at(3) }),
      stored({ id: 'd', text: 'only a cat', createdAt: at(4) }),
    )

    expect((await searchLocal(store, ANA, query('"big meeting"'), null)).hits.map((h) => h.messageId)).toEqual(['c', 'a'])
    expect((await searchLocal(store, ANA, query('meeting -draft'), null)).hits.map((h) => h.messageId)).toEqual(['b', 'a'])
    expect((await searchLocal(store, ANA, query('cat or draft'), null)).hits.map((h) => h.messageId)).toEqual(['d', 'c'])
  })

  describe('in pages', () => {
    const many = (count: number, text = 'common word') =>
      Array.from({ length: count }, (_, i) => stored({ id: `m${String(i).padStart(4, '0')}`, text, createdAt: at(i), changeSeq: i + 1 }))

    it('returns a page and where to go on from, when there are more', async () => {
      const store = await withMessages(...many(SEARCH_PAGE_SIZE + 5))

      const page = await searchLocal(store, ANA, query('common'), null)

      expect(page.hits).toHaveLength(SEARCH_PAGE_SIZE)
      expect(page.next).not.toBeNull()
      expect(page.hits[0].messageId).toBe(`m${String(SEARCH_PAGE_SIZE + 4).padStart(4, '0')}`)
    })

    it('goes on from where the page stopped, without a hit twice, and says it ran out', async () => {
      const total = SEARCH_PAGE_SIZE * 2 + 7
      const store = await withMessages(...many(total))

      const first = await searchLocal(store, ANA, query('common'), null)
      const second = await searchLocal(store, ANA, query('common'), first.next)
      const third = await searchLocal(store, ANA, query('common'), second.next)

      const ids = [...first.hits, ...second.hits, ...third.hits].map((hit) => hit.messageId)
      expect(ids).toHaveLength(total)
      expect(new Set(ids).size).toBe(total)
      expect([first.hits.length, second.hits.length, third.hits.length]).toEqual([SEARCH_PAGE_SIZE, SEARCH_PAGE_SIZE, 7])
      expect(third.next).toBeNull()
    })

    it('stops after a page, so a common word does not read the whole store', async () => {
      const store = await withMessages(...many(300))
      let visited = 0
      const counting: LocalStore = {
        ...store,
        scan: (userId, after, visit) =>
          store.scan(userId, after, (message) => {
            visited++
            return visit(message)
          }),
      }

      await searchLocal(counting, ANA, query('common'), null)

      expect(visited).toBe(SEARCH_PAGE_SIZE)
    })

    it('reads on past messages that do not match, until it has a page', async () => {
      const store = await withMessages(
        ...many(5, 'needle'),
        ...Array.from({ length: 100 }, (_, i) => stored({ id: `x${i}`, text: 'hay', createdAt: at(1000 + i), changeSeq: 1000 + i })),
      )

      const page = await searchLocal(store, ANA, query('needle'), null)

      expect(page.hits).toHaveLength(5)
      expect(page.next).toBeNull()
    })

    it('says it ran out of a full page that happened to be the last', async () => {
      const store = await withMessages(...many(SEARCH_PAGE_SIZE))

      const first = await searchLocal(store, ANA, query('common'), null)
      const second = await searchLocal(store, ANA, query('common'), first.next)

      expect(first.hits).toHaveLength(SEARCH_PAGE_SIZE)
      expect(second).toEqual({ hits: [], next: null })
    })
  })
})
