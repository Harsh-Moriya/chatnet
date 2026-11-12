import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { encryptedFileLength } from '../../../lib/crypto/file'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Message } from '../types'
import { keysDouble, opener, resetKeysDouble, sealed } from './keysDouble'
import { reopenConversation, setReopenListener } from './reopen'

// The keys of a conversation need a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('./keysDouble')).keysModule)

const store = () => useChatStore.getState()

// A message held that could not be opened when it arrived.
function unreadable(id: string, text: string, overrides: Partial<Message> = {}): Message {
  return {
    id,
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: null,
    unreadable: 'noKey',
    ciphertext: sealed(text),
    keyEpoch: 2,
    createdAt: `2026-10-11T10:00:0${id.slice(1)}Z`,
    clientMessageId: `cid-${id}`,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    changeSeq: 1,
    ...overrides,
  }
}

function conversation(overrides: Partial<Conversation> = {}): Conversation {
  return {
    id: 'c1',
    type: 'Direct',
    name: 'Bob',
    avatarUrl: null,
    updatedAt: '2026-10-11T10:00:00Z',
    lastMessage: null,
    unreadCount: 0,
    otherUserId: 'bob',
    isMuted: false,
    leftAt: null,
    keyEpoch: 2,
    keyRotationRequired: false,
    changeSeq: 0,
    ...overrides,
  }
}

beforeEach(() => {
  store().reset()
  resetKeysDouble()
})

describe('opening a conversation again once its keys may have arrived', () => {
  afterEach(() => setReopenListener(null))

  it('lets whoever keeps messages open its own again, once those held here are opened', async () => {
    const order: string[] = []
    setReopenListener(async (conversationId) => {
      order.push(`listener ${conversationId}`)
    })
    store().seedMessages('c1', [unreadable('m1', 'first')])
    opener.open.mockImplementationOnce(async (message) => {
      order.push('opened here')
      return { readable: true as const, text: message.ciphertext.slice('sealed:'.length), files: [] }
    })

    await reopenConversation('c1')

    expect(order).toEqual(['opened here', 'listener c1'])
  })

  it('lets it open its own when there is nothing here to open', async () => {
    const listener = vi.fn(async () => undefined)
    setReopenListener(listener)

    await reopenConversation('c1')

    expect(listener).toHaveBeenCalledWith('c1')
  })

  it('does not call it when the keys of this browser are not available', async () => {
    const listener = vi.fn(async () => undefined)
    setReopenListener(listener)
    keysDouble.messageOpener.mockImplementationOnce(() => {
      throw new Error('The encryption keys are not available.')
    })

    await reopenConversation('c1')

    expect(listener).not.toHaveBeenCalled()
  })

  it('tells the opener to forget that keys were missing, so it asks for them again', async () => {
    await reopenConversation('c1')

    expect(opener.forget).toHaveBeenCalledWith('c1')
  })

  it('shows the text of the messages that could not be opened before', async () => {
    store().seedMessages('c1', [unreadable('m1', 'first'), unreadable('m2', 'second')])

    await reopenConversation('c1')

    expect(store().messages.c1.map((m) => [m.content, m.unreadable])).toEqual([
      ['first', null],
      ['second', null],
    ])
  })

  it('says what the files of a message are once it can be opened', async () => {
    const entry = { name: 'one.png', type: 'image/png', size: 100, key: 'k'.repeat(43), chunkSize: 65536 }
    store().seedMessages('c1', [
      unreadable('m1', 'first', { ciphertext: sealed('first', [entry]), attachments: [{ id: 'a1', sizeBytes: encryptedFileLength(100, 65536), url: 'u', file: null }] }),
    ])

    await reopenConversation('c1')

    expect(store().messages.c1[0].attachments[0].file).toEqual(entry)
  })

  it('opens each as what it was sealed as', async () => {
    store().seedMessages('c1', [unreadable('m1', 'first', { editCount: 3, keyEpoch: 4 })])

    await reopenConversation('c1')

    expect(opener.open).toHaveBeenCalledWith({
      conversationId: 'c1',
      senderId: 'bob',
      clientMessageId: 'cid-m1',
      editCount: 3,
      keyEpoch: 4,
      ciphertext: sealed('first'),
    })
  })

  it('leaves alone a message that is shown, a deleted one, and one made here that the server has not confirmed', async () => {
    store().seedMessages('c1', [
      unreadable('m1', 'shown', { content: 'shown', unreadable: null }),
      unreadable('m2', 'gone', { ciphertext: null, isDeleted: true, unreadable: null }),
      unreadable('m3', 'mine', { clientMessageId: null }),
    ])

    await reopenConversation('c1')

    expect(opener.open).not.toHaveBeenCalled()
  })

  it('says why a message is still not shown when it still cannot be opened', async () => {
    store().seedMessages('c1', [unreadable('m1', 'first')])
    opener.open.mockResolvedValueOnce({ readable: false, reason: 'senderUnknown' } as never)

    await reopenConversation('c1')

    expect(store().messages.c1[0]).toMatchObject({ content: null, unreadable: 'senderUnknown' })
  })

  it('does not put the text on a message that was edited while it was being opened', async () => {
    store().seedMessages('c1', [unreadable('m1', 'first')])
    let release!: () => void
    let started!: () => void
    const begun = new Promise<void>((resolve) => {
      started = resolve
    })
    opener.open.mockImplementationOnce(async () => {
      started()
      await new Promise<void>((resolve) => {
        release = resolve
      })
      return { readable: true as const, text: 'first', files: [] }
    })

    const reopening = reopenConversation('c1')
    await begun
    store().replaceMessage(unreadable('m1', 'second', { editCount: 1, unreadable: 'noKey' }))
    release()
    await reopening

    expect(store().messages.c1[0]).toMatchObject({ editCount: 1, content: null, unreadable: 'noKey' })
  })

  it('opens the preview of the list too, when it was not shown', async () => {
    store().seedConversations([
      conversation({
        lastMessage: {
          senderId: 'bob',
          content: null,
          unreadable: 'noKey',
          createdAt: '2026-10-11T10:00:00Z',
          attachmentCount: 0,
          sealed: { ciphertext: sealed('the last one'), keyEpoch: 2, clientMessageId: 'cid-x', editCount: 0 },
        },
      }),
    ])

    await reopenConversation('c1')

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: 'the last one', unreadable: null })
  })

  it('leaves a preview that is shown alone', async () => {
    store().seedConversations([
      conversation({
        lastMessage: {
          senderId: 'bob',
          content: 'shown',
          unreadable: null,
          createdAt: '2026-10-11T10:00:00Z',
          attachmentCount: 0,
          sealed: { ciphertext: sealed('shown'), keyEpoch: 2, clientMessageId: 'cid-x', editCount: 0 },
        },
      }),
    ])

    await reopenConversation('c1')

    expect(opener.open).not.toHaveBeenCalled()
  })

  it('does nothing when the keys of this browser are not available', async () => {
    store().seedMessages('c1', [unreadable('m1', 'first')])
    keysDouble.messageOpener.mockImplementationOnce(() => {
      throw new Error('The encryption keys are not available.')
    })

    await reopenConversation('c1')

    expect(store().messages.c1[0]).toMatchObject({ content: null, unreadable: 'noKey' })
    expect(opener.open).not.toHaveBeenCalled()
  })
})
