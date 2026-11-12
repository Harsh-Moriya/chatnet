import { describe, expect, it, vi } from 'vitest'
import { MemoryLocalStore } from '../store/memoryLocalStore'
import { ANA, CursorAheadError, FakeServer, open } from '../testKit'
import { syncAll, type SyncDeps } from './engine'

function setUp() {
  const server = new FakeServer()
  const store = new MemoryLocalStore()
  const pending: number[] = []

  const deps = (overrides: Partial<SyncDeps> = {}): SyncDeps => ({
    store,
    userId: ANA,
    history: server.history,
    changes: server.changes,
    open,
    cursorAhead: (error) => error instanceof CursorAheadError,
    onHistoryPending: (count) => pending.push(count),
    ...overrides,
  })

  const texts = (conversationId = 'c1') =>
    [...store.messages.values()]
      .filter((message) => message.conversationId === conversationId)
      .sort((a, b) => a.changeSeq - b.changeSeq)
      .map((message) => (message.state === 'text' ? message.text : `(${message.state})`))

  return { server, store, pending, deps, texts }
}

const say = (server: FakeServer, count: number, conversationId = 'c1') => {
  for (let i = 0; i < count; i++) server.say(conversationId, `message ${i}`)
}

describe('reading a conversation for the first time', () => {
  it('reads the whole history, newest page first, and keeps every message', async () => {
    const { server, store, deps, texts } = setUp()
    say(server, 130)

    await syncAll(deps(), server.listed('c1'))

    expect(texts()).toHaveLength(130)
    expect(server.historyCalls.map((call) => call.before)).toEqual([null, expect.any(String), expect.any(String)])
    expect(store.messages.size).toBe(130)
  })

  it('asks for the history before the oldest message it holds, a page at a time', async () => {
    const { server, deps } = setUp()
    say(server, 120)

    await syncAll(deps(), server.listed('c1'))

    const history = await server.history('c1', null)
    expect(server.historyCalls[1].before).toBe(history[0].id)
  })

  it('stops after a page shorter than the server gives, and not before', async () => {
    const { server, deps } = setUp()
    say(server, 100)

    await syncAll(deps(), server.listed('c1'))

    // A full page, then another full page, then an empty one: a full page may still be the last.
    expect(server.historyCalls).toHaveLength(3)
  })

  it('records where the history was read from and that it was read, with the cursor at that point', async () => {
    const { server, store, deps } = setUp()
    say(server, 10)
    const listed = server.listed('c1')

    await syncAll(deps(), listed)

    expect(await store.readPoint(ANA, 'c1')).toMatchObject({ historyRead: true, startSeq: 10, cursor: 10 })
  })

  it('reads an empty conversation, which has no history and no changes', async () => {
    const { server, store, deps } = setUp()

    await syncAll(deps(), server.listed('c1'))

    expect(await store.readPoint(ANA, 'c1')).toMatchObject({ historyRead: true, cursor: 0, startSeq: 0 })
    expect(store.messages.size).toBe(0)
  })

  it('reads what changed while the history was being read, from the number the history began at', async () => {
    const { server, store, deps, texts } = setUp()
    say(server, 60)
    const listed = server.listed('c1')
    const history = server.history
    let first = true

    // Someone edits an old message and says something new after the first page was read.
    const during: SyncDeps['history'] = async (id, before) => {
      const page = await history(id, before)
      if (first) {
        first = false
        const oldest = (await history(id, null))[0]
        server.edit('c1', oldest.id, 'edited meanwhile')
        server.say('c1', 'said meanwhile')
      }
      return page
    }

    await syncAll(deps({ history: during }), listed)

    expect(texts()).toContain('edited meanwhile')
    expect(texts()).toContain('said meanwhile')
    expect((await store.readPoint(ANA, 'c1'))?.cursor).toBe(server.seq('c1'))
  })

  it('keeps messages that were deleted as small markers, so a late copy cannot bring them back', async () => {
    const { server, deps, texts } = setUp()
    const gone = server.say('c1', 'gone soon')
    server.say('c1', 'stays')
    server.remove('c1', gone.id)

    await syncAll(deps(), server.listed('c1'))

    expect(texts()).toEqual(['stays', '(deleted)'])
  })

  it('keeps a message that could not be opened as sealed', async () => {
    const { server, deps, texts } = setUp()
    server.say('c1', 'locked:the key is not here yet')

    await syncAll(deps(), server.listed('c1'))

    expect(texts()).toEqual(['(sealed)'])
  })

  it('reads the conversations most recently active first', async () => {
    const { server, deps } = setUp()
    say(server, 2, 'old')
    say(server, 2, 'new')

    await syncAll(deps(), server.listed('new', 'old'))

    expect(server.historyCalls.map((call) => call.conversationId)).toEqual(['new', 'old'])
  })

  it('makes the store ready for the person before anything is read', async () => {
    const { server, store, deps } = setUp()

    await syncAll(deps(), server.listed('c1'))

    expect(store.prepared).toEqual([ANA])
  })
})

describe('an interruption', () => {
  it('goes on from the oldest message read, with the same starting number, when the history was only part read', async () => {
    const { server, store, deps, texts } = setUp()
    say(server, 120)
    const listed = server.listed('c1')
    server.failHistoryAt = { call: 2, error: new Error('the connection dropped') }

    const failed = await syncAll(deps(), listed)
    const interrupted = await store.readPoint(ANA, 'c1')
    server.say('c1', 'said after the interruption')
    const second = await syncAll(deps(), server.listed('c1'))

    expect(failed.failed).toEqual(['c1'])
    expect(interrupted).toMatchObject({ historyRead: false, startSeq: 120, cursor: null })
    expect(second.failed).toEqual([])
    expect(texts()).toHaveLength(121)
    // It asked for the page just older than the one it had, not the latest again.
    expect(server.historyCalls[2].before).toBe(interrupted?.oldestId)
    expect(await store.readPoint(ANA, 'c1')).toMatchObject({ historyRead: true, startSeq: 120, cursor: 121 })
  })

  it('does not take a conversation whose history was part read for one that is up to date', async () => {
    const { server, store, deps } = setUp()
    say(server, 120)
    server.failHistoryAt = { call: 2, error: new Error('dropped') }
    await syncAll(deps(), server.listed('c1'))

    await syncAll(deps(), server.listed('c1'))

    expect((await store.readPoint(ANA, 'c1'))?.historyRead).toBe(true)
  })

  it('stops at once when it is told to, and keeps what it had read', async () => {
    const { server, store, deps } = setUp()
    say(server, 120)
    const stop = new AbortController()
    const history = server.history
    const stopping: SyncDeps['history'] = async (id, before) => {
      const page = await history(id, before)
      stop.abort()
      return page
    }

    await expect(syncAll(deps({ history: stopping, signal: stop.signal }), server.listed('c1'))).rejects.toThrow()

    expect(store.messages.size).toBe(50)
    expect((await store.readPoint(ANA, 'c1'))?.historyRead).toBe(false)
  })
})

describe('staying in step afterwards', () => {
  it('asks the feed from the cursor and applies a new message, an edit and a deletion', async () => {
    const { server, store, deps } = setUp()
    const old = server.say('c1', 'first')
    server.say('c1', 'second')
    await syncAll(deps(), server.listed('c1'))
    server.say('c1', 'third')
    server.edit('c1', old.id, 'first, edited')
    server.remove('c1', server.say('c1', 'short lived').id)

    await syncAll(deps(), server.listed('c1'))

    expect([...store.messages.values()].map((m) => (m.state === 'text' ? m.text : m.state)).sort()).toEqual(['deleted', 'first, edited', 'second', 'third'])
    expect(server.changesCalls.at(-1)).toEqual({ conversationId: 'c1', since: 2 })
  })

  it('reads the feed a page at a time until it has no more', async () => {
    const { server, store, deps } = setUp()
    server.feedPage = 3
    say(server, 2)
    await syncAll(deps(), server.listed('c1'))
    say(server, 8)
    server.changesCalls.length = 0

    await syncAll(deps(), server.listed('c1'))

    expect(server.changesCalls.map((call) => call.since)).toEqual([2, 5, 8])
    expect((await store.readPoint(ANA, 'c1'))?.cursor).toBe(10)
    expect(store.messages.size).toBe(10)
  })

  it('moves the cursor with each page, so a page that fails leaves it where the last good one did', async () => {
    const { server, store, deps } = setUp()
    server.feedPage = 3
    say(server, 1)
    await syncAll(deps(), server.listed('c1'))
    say(server, 7)
    const changes = server.changes
    let calls = 0
    const breaking: SyncDeps['changes'] = async (id, since) => {
      if (++calls === 3) throw new Error('dropped')
      return changes(id, since)
    }

    const result = await syncAll(deps({ changes: breaking }), server.listed('c1'))

    expect(result.failed).toEqual(['c1'])
    expect((await store.readPoint(ANA, 'c1'))?.cursor).toBe(7)
  })

  it('asks for nothing of a conversation that has not moved', async () => {
    const { server, deps } = setUp()
    say(server, 3)
    await syncAll(deps(), server.listed('c1'))
    server.historyCalls.length = 0
    server.changesCalls.length = 0

    await syncAll(deps(), server.listed('c1'))

    expect(server.historyCalls).toEqual([])
    expect(server.changesCalls).toEqual([])
  })

  it('asks the feed only for the conversations that moved', async () => {
    const { server, deps } = setUp()
    say(server, 2, 'quiet')
    say(server, 2, 'busy')
    await syncAll(deps(), server.listed('quiet', 'busy'))
    server.changesCalls.length = 0
    say(server, 1, 'busy')

    await syncAll(deps(), server.listed('quiet', 'busy'))

    expect(server.changesCalls.map((call) => call.conversationId)).toEqual(['busy'])
  })

  it('does not apply a copy of a message that is older than the one it holds', async () => {
    const { server, store, deps, texts } = setUp()
    const sent = server.say('c1', 'original')
    await syncAll(deps(), server.listed('c1'))
    server.edit('c1', sent.id, 'edited')
    // The feed hands over an old copy together with the new one.
    const changes = server.changes
    const stale: SyncDeps['changes'] = async (id, since) => {
      const page = await changes(id, since)
      return { ...page, changes: [{ ...sent, ciphertext: 'a stale copy', changeSeq: 1 }, ...page.changes] }
    }

    await syncAll(deps({ changes: stale }), server.listed('c1'))

    expect(texts()).toEqual(['edited'])
    expect(store.messages.size).toBe(1)
  })

  it('refuses a feed that says there is more and does not move on', async () => {
    const { server, deps } = setUp()
    say(server, 1)
    await syncAll(deps(), server.listed('c1'))
    say(server, 1)

    const result = await syncAll(deps({ changes: async () => ({ changes: [], cursor: 1, more: true }) }), server.listed('c1'))

    expect(result.failed).toEqual(['c1'])
  })
})

describe('a server that went back', () => {
  it('forgets the conversation and reads it again from the start, once', async () => {
    const { server, store, deps, texts } = setUp()
    say(server, 3)
    await syncAll(deps(), server.listed('c1'))
    // A cursor from before the server was restored.
    await store.apply(ANA, 'c1', [], { cursor: 50 })
    server.historyCalls.length = 0

    const result = await syncAll(deps(), [{ id: 'c1', changeSeq: 3 }])

    expect(result.failed).toEqual([])
    expect(server.historyCalls.length).toBeGreaterThan(0)
    expect(texts()).toHaveLength(3)
    expect(await store.readPoint(ANA, 'c1')).toMatchObject({ historyRead: true, cursor: 3 })
  })

  it('leaves a conversation that is still refused after being read again for the next run', async () => {
    const { server, deps } = setUp()
    say(server, 2)
    await syncAll(deps(), server.listed('c1'))
    server.say('c1', 'more')

    const result = await syncAll(deps({ changes: async () => Promise.reject(new CursorAheadError()) }), server.listed('c1'))

    expect(result.failed).toEqual(['c1'])
  })

  it('does not take another failure for a server that went back', async () => {
    const { server, store, deps } = setUp()
    say(server, 2)
    await syncAll(deps(), server.listed('c1'))
    server.say('c1', 'more')
    server.failChanges = new Error('the server is down')

    const result = await syncAll(deps(), server.listed('c1'))

    expect(result.failed).toEqual(['c1'])
    expect(await store.readPoint(ANA, 'c1')).toMatchObject({ historyRead: true, cursor: 2 })
  })
})

describe('when something goes wrong', () => {
  it('does not stop the other conversations because one failed', async () => {
    const { server, deps, texts } = setUp()
    say(server, 2, 'good')
    say(server, 2, 'bad')
    const history = server.history
    const failing: SyncDeps['history'] = (id, before) => (id === 'bad' ? Promise.reject(new Error('refused')) : history(id, before))

    const result = await syncAll(deps({ history: failing }), server.listed('bad', 'good'))

    expect(result.failed).toEqual(['bad'])
    expect(texts('good')).toHaveLength(2)
  })

  it('counts down the conversations whose history is still to be read, and leaves a failed one counted', async () => {
    const { server, pending, deps } = setUp()
    say(server, 1, 'a')
    say(server, 1, 'b')
    say(server, 1, 'c')
    const history = server.history
    const failing: SyncDeps['history'] = (id, before) => (id === 'b' ? Promise.reject(new Error('refused')) : history(id, before))

    await syncAll(deps({ history: failing }), server.listed('a', 'b', 'c'))

    expect(pending[0]).toBe(3)
    expect(pending.at(-1)).toBe(1)
  })

  it('reports nothing pending when everything has its history', async () => {
    const { server, pending, deps } = setUp()
    say(server, 1, 'a')

    await syncAll(deps(), server.listed('a'))
    pending.length = 0
    await syncAll(deps(), server.listed('a'))

    expect(pending).toEqual([0])
  })

  it('does nothing for an empty list', async () => {
    const { deps } = setUp()
    const history = vi.fn()

    const result = await syncAll(deps({ history }), [])

    expect(result.failed).toEqual([])
    expect(history).not.toHaveBeenCalled()
  })
})
