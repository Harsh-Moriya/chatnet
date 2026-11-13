import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { encryptedFileLength } from '../../../lib/crypto/file'
import type { FileRef } from '../../../lib/crypto/message'
import type { NotificationWire } from '../../notifications/types'
import { PREVIEW_LENGTH } from '../preview'
import type { ConversationWire, LastMessage, LastMessageWire, MessageWire } from '../types'
import { keysDouble, opener, resetKeysDouble, sealed } from './keysDouble'
import {
  conversationOf,
  inArrivalOrder,
  openLastMessage,
  openMessage,
  openMessages,
  openMessagesQuietly,
  openNotification,
  ownMessage,
  sealedOf,
  setOpenedSink,
} from './openWire'

// The keys of a conversation need a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('./keysDouble')).keysModule)

function wireMessage(overrides: Partial<MessageWire> = {}): MessageWire {
  return {
    id: 'm1',
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    ciphertext: sealed('hello'),
    keyEpoch: 2,
    createdAt: '2026-10-11T10:00:00Z',
    attachments: [],
    clientMessageId: 'cid-1',
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    changeSeq: 1,
    ...overrides,
  }
}

function lastWire(overrides: Partial<LastMessageWire> = {}): LastMessageWire {
  return {
    senderId: 'bob',
    ciphertext: sealed('hello'),
    keyEpoch: 2,
    clientMessageId: 'cid-1',
    editCount: 0,
    createdAt: '2026-10-11T10:00:00Z',
    attachmentCount: 0,
    ...overrides,
  }
}

function conversationWire(lastMessage: LastMessageWire | null): ConversationWire {
  return {
    id: 'c1',
    type: 'Direct',
    name: 'Bob',
    avatarUrl: null,
    updatedAt: '2026-10-11T10:00:00Z',
    lastMessage,
    unreadCount: 0,
    otherUserId: 'bob',
    isMuted: false,
    leftAt: null,
    keyEpoch: 2,
    keyRotationRequired: false,
    changeSeq: 0,
  }
}

function notificationWire(overrides: Partial<NotificationWire> = {}): NotificationWire {
  return {
    id: 'n1',
    type: 'Mention',
    conversationId: 'c1',
    actorId: 'bob',
    actorDisplayName: 'Bob',
    messageId: 'm1',
    message: { ciphertext: sealed('@me hello'), keyEpoch: 2, clientMessageId: 'cid-1', editCount: 0 },
    isRead: false,
    createdAt: '2026-10-11T10:00:00Z',
    ...overrides,
  }
}

// A file as the sealed message describes it, and as the server holds it once stored.
const entry = (name: string, size: number): FileRef => ({ name, type: 'image/png', size, key: 'k'.repeat(43), chunkSize: 65536 })
const storedFile = (id: string, size: number) => ({ id, sizeBytes: encryptedFileLength(size, 65536), url: `https://files.test/${id}` })

beforeEach(() => {
  resetKeysDouble()
})

// ##### Messages #####

describe('opening a message', () => {
  it('gives its text and keeps everything else as the server sent it', async () => {
    const wire = wireMessage()

    const message = await openMessage(wire)

    expect(message).toEqual({ ...wire, content: 'hello', unreadable: null })
  })

  it('opens it as what it was sealed as: for this conversation and sender, this id, this edit and this epoch', async () => {
    await openMessage(wireMessage({ editCount: 3, keyEpoch: 5 }))

    expect(opener.open).toHaveBeenCalledWith({
      conversationId: 'c1',
      senderId: 'bob',
      clientMessageId: 'cid-1',
      editCount: 3,
      keyEpoch: 5,
      ciphertext: sealed('hello'),
    })
  })

  it('has no text for a deleted message, and has nothing to open', async () => {
    const message = await openMessage(wireMessage({ ciphertext: null, isDeleted: true }))

    expect(message).toMatchObject({ content: null, unreadable: null, isDeleted: true })
    expect(opener.open).not.toHaveBeenCalled()
  })

  it.each(['noKey', 'invalid', 'senderUnknown', 'senderChanged'] as const)('says why it cannot be read: %s', async (reason) => {
    opener.open.mockResolvedValueOnce({ readable: false as never, reason } as never)

    const message = await openMessage(wireMessage())

    expect(message).toMatchObject({ content: null, unreadable: reason })
  })

  it('says there is no key when the keys of this browser fail to open it', async () => {
    opener.open.mockRejectedValueOnce(new Error('the store could not be read'))

    expect(await openMessage(wireMessage())).toMatchObject({ content: null, unreadable: 'noKey' })
  })

  it('says there is no key when nobody is signed in, so there is no opener at all', async () => {
    keysDouble.messageOpener.mockImplementationOnce(() => {
      throw new Error('The encryption keys are not available.')
    })

    expect(await openMessage(wireMessage())).toMatchObject({ content: null, unreadable: 'noKey' })
  })

  it('opens a page of them together, in the order they came, and one that cannot be opened leaves the others alone', async () => {
    const page = [wireMessage({ id: 'm1' }), wireMessage({ id: 'm2', ciphertext: 'not sealed here' }), wireMessage({ id: 'm3', ciphertext: sealed('third') })]

    const messages = await openMessages(page)

    expect(messages.map((m) => [m.id, m.content, m.unreadable])).toEqual([
      ['m1', 'hello', null],
      ['m2', null, 'invalid'],
      ['m3', 'third', null],
    ])
  })
})

describe('opening the files of a message', () => {
  it('says what each file is from the sealed message, pairing the n-th file with the n-th entry', async () => {
    const files = [entry('one.png', 100), entry('two.png', 200)]

    const message = await openMessage(
      wireMessage({ ciphertext: sealed('look', files), attachments: [storedFile('a1', 100), storedFile('a2', 200)] }),
    )

    expect(message.attachments.map((a) => [a.id, a.file])).toEqual([
      ['a1', files[0]],
      ['a2', files[1]],
    ])
  })

  it('does not say what a file is when the sealed message describes a file of another size', async () => {
    const message = await openMessage(wireMessage({ ciphertext: sealed('look', [entry('one.png', 100)]), attachments: [storedFile('a1', 101)] }))

    expect(message.attachments[0].file).toBeNull()
  })

  it('does not say what a file is when the server holds more files than the sealed message describes', async () => {
    const message = await openMessage(
      wireMessage({ ciphertext: sealed('look', [entry('one.png', 100)]), attachments: [storedFile('a1', 100), storedFile('a2', 100)] }),
    )

    expect(message.attachments.map((a) => a.file?.name ?? null)).toEqual(['one.png', null])
  })

  it('does not say what a file is when the message lists none, as one sealed before files were sealed does', async () => {
    const message = await openMessage(wireMessage({ ciphertext: sealed('look'), attachments: [storedFile('a1', 100)] }))

    expect(message).toMatchObject({ content: 'look' })
    expect(message.attachments[0].file).toBeNull()
  })

  it('does not say what a file is when the message cannot be opened', async () => {
    opener.open.mockResolvedValueOnce({ readable: false as never, reason: 'noKey' } as never)

    const message = await openMessage(wireMessage({ attachments: [storedFile('a1', 100)] }))

    expect(message).toMatchObject({ content: null, unreadable: 'noKey' })
    expect(message.attachments[0]).toMatchObject({ id: 'a1', file: null })
  })

  it('does not say what a file is for a deleted message, which has nothing to open', async () => {
    const message = await openMessage(wireMessage({ ciphertext: null, isDeleted: true, attachments: [storedFile('a1', 100)] }))

    expect(message.attachments[0].file).toBeNull()
  })

  it('keeps the link and the stored size of each file as the server sent them', async () => {
    const message = await openMessage(wireMessage({ ciphertext: sealed('x', [entry('one.png', 100)]), attachments: [storedFile('a1', 100)] }))

    expect(message.attachments[0]).toMatchObject({ id: 'a1', sizeBytes: encryptedFileLength(100, 65536), url: 'https://files.test/a1' })
  })
})

describe('handing on what is opened to whoever keeps it', () => {
  afterEach(() => setOpenedSink(null))

  it('hands on a message that is opened, as it is shown', async () => {
    const sink = vi.fn()
    setOpenedSink(sink)

    const message = await openMessage(wireMessage({ ciphertext: sealed('kept') }))

    expect(sink).toHaveBeenCalledTimes(1)
    expect(sink).toHaveBeenCalledWith(message)
    expect(message.content).toBe('kept')
  })

  it('hands on every message of a page', async () => {
    const sink = vi.fn()
    setOpenedSink(sink)

    await openMessages([wireMessage({ id: 'm1' }), wireMessage({ id: 'm2', ciphertext: 'not sealed here' }), wireMessage({ id: 'm3' })])

    expect(sink.mock.calls.map(([message]) => message.id)).toEqual(['m1', 'm2', 'm3'])
  })

  it('hands on a message that could not be opened, and a deleted one, as they are', async () => {
    const sink = vi.fn()
    setOpenedSink(sink)

    await openMessage(wireMessage({ id: 'lost', ciphertext: 'not sealed here' }))
    await openMessage(wireMessage({ id: 'gone', ciphertext: null, isDeleted: true }))

    expect(sink.mock.calls[0][0]).toMatchObject({ id: 'lost', content: null, unreadable: 'invalid' })
    expect(sink.mock.calls[1][0]).toMatchObject({ id: 'gone', isDeleted: true })
  })

  it('hands on the message this tab sealed itself, once the server has confirmed it', () => {
    const sink = vi.fn()
    setOpenedSink(sink)

    const message = ownMessage(wireMessage({ ciphertext: sealed('what the server stored') }), 'what was written', [])

    expect(sink).toHaveBeenCalledWith(message)
    expect(message.content).toBe('what was written')
  })

  it('does not hand on what is opened for the one that keeps it, which writes it itself', async () => {
    const sink = vi.fn()
    setOpenedSink(sink)

    const opened = await openMessagesQuietly([wireMessage({ ciphertext: sealed('quiet') })])

    expect(sink).not.toHaveBeenCalled()
    expect(opened[0].content).toBe('quiet')
  })

  it('shows the message even when what keeps it fails', async () => {
    setOpenedSink(() => {
      throw new Error('the disk is full')
    })

    const message = await openMessage(wireMessage({ ciphertext: sealed('still shown') }))

    expect(message.content).toBe('still shown')
  })

  it('stops handing on once it is told there is no one', async () => {
    const sink = vi.fn()
    setOpenedSink(sink)
    setOpenedSink(null)

    await openMessage(wireMessage())

    expect(sink).not.toHaveBeenCalled()
  })

  it('does not hand on a notification or a preview, which are not messages of a conversation', async () => {
    const sink = vi.fn()
    setOpenedSink(sink)

    await openNotification(notificationWire())
    await openLastMessage('c1', conversationOf(conversationWire(lastWire())).lastMessage!)

    expect(sink).not.toHaveBeenCalled()
  })
})

describe('a message this tab sealed itself', () => {
  it('has the text that was written, and is not opened again', async () => {
    const message = ownMessage(wireMessage({ ciphertext: sealed('what the server stored') }), 'what was written', [])

    expect(message).toMatchObject({ content: 'what was written', unreadable: null })
    expect(opener.open).not.toHaveBeenCalled()
  })

  it('knows what its files are from what was sealed, and checks them against what the server stored', () => {
    const files = [entry('one.png', 100), entry('two.png', 200)]

    const message = ownMessage(wireMessage({ attachments: [storedFile('a1', 100), storedFile('a2', 5)] }), 'x', files)

    expect(message.attachments.map((a) => a.file?.name ?? null)).toEqual(['one.png', null])
  })
})

describe('sealing a message held again, to open it once its key arrives', () => {
  it('gives the files as the server sent them, which say nothing of what they are', async () => {
    const message = await openMessage(wireMessage({ ciphertext: sealed('x', [entry('one.png', 100)]), attachments: [storedFile('a1', 100)] }))

    expect(sealedOf(message)?.attachments).toEqual([storedFile('a1', 100)])
  })

  it('gives back what the server sent', async () => {
    const wire = wireMessage({ attachments: [], editCount: 2, editedAt: '2026-10-11T10:05:00Z' })

    expect(sealedOf(await openMessage(wire))).toEqual(wire)
  })

  it('gives nothing for a deleted message', async () => {
    expect(sealedOf(await openMessage(wireMessage({ ciphertext: null, isDeleted: true })))).toBeNull()
  })

  it('gives nothing for a message made here that the server has not confirmed', async () => {
    const message = await openMessage(wireMessage())

    expect(sealedOf({ ...message, clientMessageId: null })).toBeNull()
    expect(sealedOf({ ...message, ciphertext: undefined })).toBeNull()
    expect(sealedOf({ ...message, keyEpoch: undefined })).toBeNull()
  })
})

// ##### The list #####

describe('the preview of a conversation as the list first shows it', () => {
  it('is a conversation with no message when it has none', () => {
    expect(conversationOf(conversationWire(null)).lastMessage).toBeNull()
  })

  it('says it is being opened, and keeps the message as sealed to open it', () => {
    const { lastMessage } = conversationOf(conversationWire(lastWire({ attachmentCount: 2 })))

    expect(lastMessage).toEqual({
      senderId: 'bob',
      createdAt: '2026-10-11T10:00:00Z',
      attachmentCount: 2,
      content: null,
      unreadable: 'pending',
      sealed: { ciphertext: sealed('hello'), keyEpoch: 2, clientMessageId: 'cid-1', editCount: 0 },
    })
  })

  it('has nothing to open when the last message was deleted', () => {
    const { lastMessage } = conversationOf(conversationWire(lastWire({ ciphertext: null })))

    expect(lastMessage).toMatchObject({ content: null, unreadable: null, sealed: null })
  })

  it('does not carry the sealed fields of the wire anywhere else', () => {
    const conversation = conversationOf(conversationWire(lastWire()))

    expect(conversation.lastMessage).not.toHaveProperty('ciphertext')
    expect(conversation.lastMessage).not.toHaveProperty('keyEpoch')
  })
})

describe('opening the preview of a conversation', () => {
  const pending = (overrides: Partial<LastMessageWire> = {}): LastMessage => conversationOf(conversationWire(lastWire(overrides))).lastMessage!

  it('shows the start of the text', async () => {
    const opened = await openLastMessage('c1', pending({ ciphertext: sealed('x'.repeat(PREVIEW_LENGTH + 50)) }))

    expect(opened.content).toBe('x'.repeat(PREVIEW_LENGTH))
    expect(opened.unreadable).toBeNull()
  })

  it('opens it as what it was sealed as, for the conversation it is in and who sent it', async () => {
    await openLastMessage('c1', pending({ senderId: 'carol', editCount: 4, keyEpoch: 7 }))

    expect(opener.open).toHaveBeenCalledWith({
      conversationId: 'c1',
      senderId: 'carol',
      ciphertext: sealed('hello'),
      keyEpoch: 7,
      clientMessageId: 'cid-1',
      editCount: 4,
    })
  })

  it('keeps what it was sealed as, so it can be opened again', async () => {
    const opened = await openLastMessage('c1', pending())

    expect(opened.sealed).toEqual({ ciphertext: sealed('hello'), keyEpoch: 2, clientMessageId: 'cid-1', editCount: 0 })
  })

  it('says why it cannot be shown', async () => {
    opener.open.mockResolvedValueOnce({ readable: false as never, reason: 'noKey' } as never)

    expect(await openLastMessage('c1', pending())).toMatchObject({ content: null, unreadable: 'noKey' })
  })

  it('is left as it is when there is nothing sealed to open', async () => {
    const deleted = pending({ ciphertext: null })

    expect(await openLastMessage('c1', deleted)).toBe(deleted)
    expect(opener.open).not.toHaveBeenCalled()
  })
})

// ##### Notifications #####

describe('opening a notification', () => {
  it('shows the start of the message that mentions the person', async () => {
    const item = await openNotification(notificationWire())

    expect(item).toEqual({
      id: 'n1',
      type: 'Mention',
      conversationId: 'c1',
      actorId: 'bob',
      actorDisplayName: 'Bob',
      messageId: 'm1',
      messagePreview: '@me hello',
      isRead: false,
      createdAt: '2026-10-11T10:00:00Z',
    })
  })

  it('opens the message as sealed by the person who did it, in the conversation it is about', async () => {
    await openNotification(notificationWire({ actorId: 'dana', conversationId: 'c9' }))

    expect(opener.open).toHaveBeenCalledWith({
      conversationId: 'c9',
      senderId: 'dana',
      ciphertext: sealed('@me hello'),
      keyEpoch: 2,
      clientMessageId: 'cid-1',
      editCount: 0,
    })
  })

  it('has no preview when the message cannot be opened', async () => {
    opener.open.mockResolvedValueOnce({ readable: false as never, reason: 'senderChanged' } as never)

    expect(await openNotification(notificationWire())).toMatchObject({ messagePreview: null })
  })

  it('has no preview for one about no message (an invitation, or a message that is gone), and opens nothing', async () => {
    const item = await openNotification(notificationWire({ type: 'MemberAdded', messageId: null, message: null }))

    expect(item.messagePreview).toBeNull()
    expect(opener.open).not.toHaveBeenCalled()
  })

  it('has no preview for one about a conversation that is gone, and opens nothing', async () => {
    const item = await openNotification(notificationWire({ conversationId: null }))

    expect(item.messagePreview).toBeNull()
    expect(opener.open).not.toHaveBeenCalled()
  })

  it('does not carry the sealed fields on', async () => {
    expect(await openNotification(notificationWire())).not.toHaveProperty('message')
  })
})

// ##### Order #####

describe('taking pushes in the order they arrived', () => {
  it('runs the jobs of one conversation one after another', async () => {
    const order: string[] = []
    const first = inArrivalOrder('c1', async () => {
      await new Promise((resolve) => setTimeout(resolve, 20))
      order.push('first')
    })
    const second = inArrivalOrder('c1', async () => {
      order.push('second')
    })

    await Promise.all([first, second])

    expect(order).toEqual(['first', 'second'])
  })

  it('does not hold one conversation up for another', async () => {
    const order: string[] = []
    const slow = inArrivalOrder('c1', async () => {
      await new Promise((resolve) => setTimeout(resolve, 20))
      order.push('slow')
    })
    const quick = inArrivalOrder('c2', async () => {
      order.push('quick')
    })

    await Promise.all([slow, quick])

    expect(order).toEqual(['quick', 'slow'])
  })
})
