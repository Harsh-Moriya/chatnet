import { describe, expect, it, vi } from 'vitest'
import type { Message } from '../chat/types'
import { createIngest, storedFrom } from './ingest'
import { MemoryLocalStore } from './store/memoryLocalStore'
import { ANA } from './testKit'

function message(overrides: Partial<Message> = {}): Message {
  return {
    id: 'm1',
    conversationId: 'c1',
    senderId: 'ben',
    senderDisplayName: 'Ben',
    content: 'Hello World',
    unreadable: null,
    ciphertext: 'AQEAAAAB',
    keyEpoch: 1,
    createdAt: '2026-11-12T10:00:00.5Z',
    attachments: [],
    clientMessageId: 'cid-1',
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    changeSeq: 4,
    ...overrides,
  }
}

const file = (name: string, size = 100) => ({
  id: `a-${name}`,
  sizeBytes: size + 29,
  url: 'https://files.test/a',
  file: { name, type: 'image/png', size, key: 'k'.repeat(43), chunkSize: 65536 },
})

describe('what a message becomes in the store', () => {
  it('keeps the text, the words of it in order, and who said it and when', () => {
    expect(storedFrom(ANA, message())).toEqual({
      userId: ANA,
      conversationId: 'c1',
      id: 'm1',
      senderId: 'ben',
      senderName: 'Ben',
      createdAt: '2026-11-12T10:00:00.5Z',
      at: Date.parse('2026-11-12T10:00:00.5Z'),
      editCount: 0,
      changeSeq: 4,
      state: 'text',
      text: 'Hello World',
      words: ['hello', 'world'],
      files: [],
    })
  })

  it('keeps a message of only files with an empty text', () => {
    expect(storedFrom(ANA, message({ content: '', attachments: [file('map.png')] }))).toMatchObject({ state: 'text', text: '', words: [] })
  })

  it('keeps the names of the files the sealed message vouches for, cleaned, with their words', () => {
    const kept = storedFrom(ANA, message({ attachments: [file('C:\\docs\\Q3 plan.pdf'), file('notes_2.txt')] }))

    expect(kept).toMatchObject({ files: [{ name: 'Q3 plan.pdf', words: ['q3', 'plan', 'pdf'] }, { name: 'notes_2.txt', words: ['notes_2', 'txt'] }] })
  })

  it('leaves out a file nothing vouches for, which has no name to search', () => {
    const unvouched = { id: 'a1', sizeBytes: 50, url: 'u', file: null }

    expect(storedFrom(ANA, message({ attachments: [unvouched, file('known.png')] }))).toMatchObject({ files: [{ name: 'known.png' }] })
  })

  it('keeps a deleted message without its text', () => {
    const kept = storedFrom(ANA, message({ content: null, ciphertext: null, isDeleted: true, changeSeq: 9 }))

    expect(kept).toMatchObject({ state: 'deleted', changeSeq: 9, id: 'm1' })
    expect(kept).not.toHaveProperty('text')
  })

  it('keeps a message that could not be opened as it was sealed, with the sizes of its files', () => {
    const kept = storedFrom(
      ANA,
      message({ content: null, unreadable: 'noKey', ciphertext: 'AQEAAAABxyz', keyEpoch: 3, clientMessageId: 'cid-9', attachments: [file('a.png', 10)] }),
    )

    expect(kept).toMatchObject({
      state: 'sealed',
      sealed: { ciphertext: 'AQEAAAABxyz', keyEpoch: 3, clientMessageId: 'cid-9', files: [{ id: 'a-a.png', sizeBytes: 39 }] },
    })
  })

  it('keeps nothing of a message the server has not numbered, which is not confirmed', () => {
    expect(storedFrom(ANA, message({ changeSeq: undefined }))).toBeNull()
  })

  it('keeps nothing of a message that could not be opened and has nothing sealed to open later', () => {
    expect(storedFrom(ANA, message({ content: null, unreadable: 'noKey', ciphertext: null }))).toBeNull()
    expect(storedFrom(ANA, message({ content: null, unreadable: 'noKey', clientMessageId: null }))).toBeNull()
  })

  it('orders messages by the time they were sent, in milliseconds', () => {
    expect(storedFrom(ANA, message({ createdAt: '2026-11-12T10:00:00.5Z' }))?.at).toBeLessThan(storedFrom(ANA, message({ createdAt: '2026-11-12T10:00:01Z' }))!.at)
  })
})

describe('writing the messages the app opens', () => {
  function ingestWith(options: { userId?: string | undefined; fail?: Error } = {}) {
    const store = new MemoryLocalStore()
    const work: (() => void)[] = []
    const errors: unknown[] = []
    const userId = 'userId' in options ? options.userId : ANA

    if (options.fail) vi.spyOn(store, 'apply').mockRejectedValueOnce(options.fail)

    const ingest = createIngest({ store, userId: () => userId, onError: (error) => errors.push(error), schedule: (job) => work.push(job) })
    return { ingest, store, work, errors }
  }

  it('gathers what is opened meanwhile and writes it once', async () => {
    const { ingest, store, work } = ingestWith()
    const apply = vi.spyOn(store, 'apply')

    ingest.remember(message({ id: 'a' }))
    ingest.remember(message({ id: 'b' }))
    ingest.remember(message({ id: 'c' }))

    expect(work).toHaveLength(1)
    expect(store.messages.size).toBe(0)

    work[0]()
    await ingest.flush()

    expect(apply).toHaveBeenCalledTimes(1)
    expect([...store.messages.values()].map((m) => m.id).sort()).toEqual(['a', 'b', 'c'])
  })

  it('writes the messages of each conversation on their own', async () => {
    const { ingest, store } = ingestWith()
    const apply = vi.spyOn(store, 'apply')

    ingest.remember(message({ id: 'a', conversationId: 'c1' }))
    ingest.remember(message({ id: 'b', conversationId: 'c2' }))
    await ingest.flush()

    expect(apply.mock.calls.map((call) => call[1]).sort()).toEqual(['c1', 'c2'])
  })

  it('moves no read point: what is opened live is not a page that was read', async () => {
    const { ingest, store } = ingestWith()

    ingest.remember(message())
    await ingest.flush()

    expect(store.points.size).toBe(0)
  })

  it('keeps only the newest copy of a message that was opened twice meanwhile', async () => {
    const { ingest, store } = ingestWith()

    ingest.remember(message({ content: 'new', changeSeq: 9 }))
    ingest.remember(message({ content: 'old', changeSeq: 3 }))
    await ingest.flush()

    expect([...store.messages.values()]).toMatchObject([{ text: 'new', changeSeq: 9 }])
  })

  it('schedules a write again after the last one was done', async () => {
    const { ingest, work } = ingestWith()

    ingest.remember(message({ id: 'a' }))
    await ingest.flush()
    ingest.remember(message({ id: 'b' }))

    expect(work).toHaveLength(2)
  })

  it('writes nothing, and schedules nothing, when nobody is signed in', async () => {
    const { ingest, store, work } = ingestWith({ userId: undefined })

    ingest.remember(message())
    await ingest.flush()

    expect(work).toHaveLength(0)
    expect(store.messages.size).toBe(0)
  })

  it('ignores a message there is nothing to keep of', async () => {
    const { ingest, work } = ingestWith()

    ingest.remember(message({ changeSeq: undefined }))

    expect(work).toHaveLength(0)
  })

  it('says a write failed and goes on with the next one', async () => {
    const failure = new Error('quota')
    const { ingest, store, errors } = ingestWith({ fail: failure })

    ingest.remember(message({ id: 'lost' }))
    await ingest.flush()
    ingest.remember(message({ id: 'kept' }))
    await ingest.flush()

    expect(errors).toEqual([failure])
    expect([...store.messages.values()].map((m) => m.id)).toEqual(['kept'])
  })

  it('writes one batch after another, so a later page never lands before an earlier one', async () => {
    const { ingest, store } = ingestWith()
    const order: string[] = []
    const apply = store.apply.bind(store)
    vi.spyOn(store, 'apply').mockImplementation(async (userId, conversationId, messages, change) => {
      order.push(messages.map((m) => m.id).join(','))
      await new Promise((resolve) => setTimeout(resolve, messages[0].id === 'first' ? 20 : 0))
      return apply(userId, conversationId, messages, change)
    })

    ingest.remember(message({ id: 'first' }))
    const one = ingest.flush()
    ingest.remember(message({ id: 'second' }))
    const two = ingest.flush()
    await Promise.all([one, two])

    expect(order).toEqual(['first', 'second'])
  })

  it('writes what was gathered when it is told to, even before the time is up', async () => {
    const { ingest, store } = ingestWith()

    ingest.remember(message({ id: 'a' }))
    await ingest.flush()

    expect(store.messages.size).toBe(1)
  })
})
