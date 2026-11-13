import { describe, expect, it, vi } from 'vitest'
import type { Message, MessageWire } from '../../chat/types'
import { MemoryLocalStore } from '../store/memoryLocalStore'
import { ANA, deletedStored, open, sealedStored, stored } from '../testKit'
import { reopenStored } from './reopenStored'

describe('opening again what the store holds sealed', () => {
  it('opens the messages that could not be opened and keeps them as text', async () => {
    const store = new MemoryLocalStore()
    await store.apply(ANA, 'c1', [sealedStored({ id: 'a', conversationId: 'c1', changeSeq: 3, sealed: { ciphertext: 'now readable', keyEpoch: 2, clientMessageId: 'cid-a', files: [] } })])

    await reopenStored(store, ANA, 'c1', open)

    expect(store.messages.get(`${ANA}|c1|a`)).toMatchObject({ state: 'text', text: 'now readable', changeSeq: 3 })
  })

  it('hands the opener each message as it was sealed, with its change number and the sizes of its files', async () => {
    const store = new MemoryLocalStore()
    await store.apply(ANA, 'c1', [
      sealedStored({ id: 'a', conversationId: 'c1', changeSeq: 7, editCount: 2, sealed: { ciphertext: 'blob', keyEpoch: 4, clientMessageId: 'cid-a', files: [{ id: 'f1', sizeBytes: 129 }] } }),
    ])
    const opener = vi.fn(async (wires: MessageWire[]) => open(wires))

    await reopenStored(store, ANA, 'c1', opener)

    expect(opener.mock.calls[0][0]).toEqual([
      expect.objectContaining({
        id: 'a',
        conversationId: 'c1',
        ciphertext: 'blob',
        keyEpoch: 4,
        clientMessageId: 'cid-a',
        editCount: 2,
        changeSeq: 7,
        isDeleted: false,
        attachments: [{ id: 'f1', sizeBytes: 129, url: '' }],
      }),
    ])
  })

  it('leaves a message that still cannot be opened as it was', async () => {
    const store = new MemoryLocalStore()
    await store.apply(ANA, 'c1', [sealedStored({ id: 'a', conversationId: 'c1', sealed: { ciphertext: 'locked:no key', keyEpoch: 2, clientMessageId: 'cid', files: [] } })])

    await reopenStored(store, ANA, 'c1', open)

    expect(store.messages.get(`${ANA}|c1|a`)?.state).toBe('sealed')
  })

  it('opens the ones it can and leaves the others', async () => {
    const store = new MemoryLocalStore()
    await store.apply(ANA, 'c1', [
      sealedStored({ id: 'a', conversationId: 'c1', sealed: { ciphertext: 'readable', keyEpoch: 1, clientMessageId: 'a', files: [] } }),
      sealedStored({ id: 'b', conversationId: 'c1', sealed: { ciphertext: 'locked:x', keyEpoch: 9, clientMessageId: 'b', files: [] } }),
    ])

    await reopenStored(store, ANA, 'c1', open)

    expect(store.messages.get(`${ANA}|c1|a`)?.state).toBe('text')
    expect(store.messages.get(`${ANA}|c1|b`)?.state).toBe('sealed')
  })

  it('does not touch the messages of other conversations or the ones that were opened or deleted', async () => {
    const store = new MemoryLocalStore()
    await store.apply(ANA, 'c1', [stored({ id: 'text', conversationId: 'c1', text: 'kept' }), deletedStored({ id: 'gone', conversationId: 'c1' })])
    await store.apply(ANA, 'c2', [sealedStored({ id: 'elsewhere', conversationId: 'c2', sealed: { ciphertext: 'readable', keyEpoch: 1, clientMessageId: 'x', files: [] } })])
    const opener = vi.fn(async (wires: MessageWire[]) => open(wires))

    await reopenStored(store, ANA, 'c1', opener)

    expect(opener).not.toHaveBeenCalled()
    expect(store.messages.get(`${ANA}|c2|elsewhere`)?.state).toBe('sealed')
    expect(store.messages.get(`${ANA}|c1|text`)).toMatchObject({ text: 'kept' })
  })

  it('writes nothing when nothing could be opened', async () => {
    const store = new MemoryLocalStore()
    await store.apply(ANA, 'c1', [sealedStored({ id: 'a', conversationId: 'c1', sealed: { ciphertext: 'locked:x', keyEpoch: 1, clientMessageId: 'a', files: [] } })])
    const apply = vi.spyOn(store, 'apply')

    await reopenStored(store, ANA, 'c1', open)

    expect(apply).not.toHaveBeenCalled()
  })

  it('keeps the names of the files of a message it opens, which are known once the sizes agree', async () => {
    const store = new MemoryLocalStore()
    await store.apply(ANA, 'c1', [
      sealedStored({ id: 'a', conversationId: 'c1', sealed: { ciphertext: 'with a file', keyEpoch: 1, clientMessageId: 'a', files: [{ id: 'f1', sizeBytes: 129 }] } }),
    ])
    const opener = async (wires: MessageWire[]): Promise<Message[]> =>
      (await open(wires)).map((message) => ({
        ...message,
        attachments: message.attachments.map((attachment) => ({
          ...attachment,
          file: { name: 'plan.pdf', type: 'application/pdf', size: 100, key: 'k'.repeat(43), chunkSize: 65536 },
        })),
      }))

    await reopenStored(store, ANA, 'c1', opener)

    expect(store.messages.get(`${ANA}|c1|a`)).toMatchObject({ state: 'text', files: [{ name: 'plan.pdf', words: ['plan', 'pdf'] }] })
  })
})
