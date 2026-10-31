import { beforeEach, describe, expect, it } from 'vitest'
import { useChatStore } from './chatStore'
import type { Conversation, Message } from '../types'

const store = () => useChatStore.getState()

function message(overrides: Partial<Message> & { id: string }): Message {
  return {
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: `content of ${overrides.id}`,
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    ...overrides,
  }
}

function conversation(overrides: Partial<Conversation> & { id: string }): Conversation {
  return {
    type: 'Direct',
    name: `Conversation ${overrides.id}`,
    avatarUrl: null,
    updatedAt: '2026-10-11T09:00:00Z',
    lastMessage: null,
    unreadCount: 0,
    otherUserId: null,
    isMuted: false,
    ...overrides,
  }
}

const previewOf = (overrides = {}) => ({
  senderId: 'bob',
  content: 'content of m1',
  createdAt: '2026-10-11T10:00:00Z',
  attachmentCount: 0,
  ...overrides,
})

beforeEach(() => {
  store().reset()
})

// ##### Messages that change after they were sent #####

describe('replaceMessage', () => {
  const edited = (overrides: Partial<Message> = {}) =>
    message({ id: 'm1', content: 'edited', editCount: 1, editedAt: '2026-10-11T10:05:00Z', ...overrides })

  it('puts the later version in place of the held one, keeping its position', () => {
    store().seedMessages('c1', [
      message({ id: 'm0', createdAt: '2026-10-11T09:00:00Z' }),
      message({ id: 'm1' }),
      message({ id: 'm2', createdAt: '2026-10-11T11:00:00Z' }),
    ])

    store().replaceMessage(edited())

    expect(store().messages.c1.map((m) => [m.id, m.content])).toEqual([
      ['m0', 'content of m0'],
      ['m1', 'edited'],
      ['m2', 'content of m2'],
    ])
  })

  it('ignores a copy that is the same version or older, and returns the same state', () => {
    store().seedMessages('c1', [edited({ editCount: 2, content: 'second edit' })])
    const before = store().messages

    store().replaceMessage(edited({ editCount: 2, content: 'same version' }))
    store().replaceMessage(edited({ editCount: 1, content: 'older' }))
    store().replaceMessage(message({ id: 'm1', editCount: 0, content: 'original' }))

    expect(store().messages).toBe(before)
    expect(store().messages.c1[0].content).toBe('second edit')
  })

  it('applies a deletion over any number of edits, and never takes it back', () => {
    store().seedMessages('c1', [edited({ editCount: 3 })])

    store().replaceMessage(message({ id: 'm1', isDeleted: true, content: null }))
    store().replaceMessage(edited({ editCount: 9, content: 'back from the dead' }))

    expect(store().messages.c1[0]).toMatchObject({ isDeleted: true, content: null })
  })

  it('ignores a message that is not held: loading its page brings it as it is', () => {
    store().seedMessages('c1', [message({ id: 'm0' })])
    const before = store().messages

    store().replaceMessage(edited({ id: 'elsewhere' }))
    store().replaceMessage(edited({ conversationId: 'c9' }))

    expect(store().messages).toBe(before)
  })

  it('keeps the id this tab invented for its own copy', () => {
    store().seedMessages('c1', [message({ id: 'm1', clientMessageId: 'p1' })])

    store().replaceMessage(edited({ clientMessageId: null }))

    expect(store().messages.c1[0].clientMessageId).toBe('p1')
  })

  it('updates the sidebar preview when it shows that message, and not otherwise', () => {
    store().seedConversations([conversation({ id: 'c1', lastMessage: previewOf() })])
    store().seedMessages('c1', [message({ id: 'm1' }), message({ id: 'm2', createdAt: '2026-10-11T11:00:00Z' })])

    store().replaceMessage(edited())
    expect(store().conversations.c1.lastMessage?.content).toBe('edited')

    const shown = store().conversations.c1
    store().replaceMessage(message({ id: 'm2', createdAt: '2026-10-11T11:00:00Z', editCount: 1, content: 'other' }))
    expect(store().conversations.c1).toBe(shown)
  })

  it('does not move the conversation in the sidebar', () => {
    store().seedConversations([
      conversation({ id: 'c1', updatedAt: '2026-10-11T12:00:00Z', lastMessage: previewOf() }),
    ])
    store().seedMessages('c1', [message({ id: 'm1' })])

    store().replaceMessage(edited())

    expect(store().conversations.c1.updatedAt).toBe('2026-10-11T12:00:00Z')
  })

  it('takes a deleted unread message off the unread count, once', () => {
    store().seedConversations([conversation({ id: 'c1', unreadCount: 2 })])
    store().seedMessages('c1', [
      message({ id: 'm1', createdAt: '2026-10-11T10:00:00Z' }),
      message({ id: 'm2', createdAt: '2026-10-11T10:01:00Z' }),
    ])
    const deleted = message({ id: 'm2', createdAt: '2026-10-11T10:01:00Z', isDeleted: true, content: null })

    store().replaceMessage(deleted, 'me')
    store().replaceMessage(deleted, 'me')

    expect(store().conversations.c1.unreadCount).toBe(1)
  })

  it('leaves the unread count alone for an edit, and when it does not know who the reader is', () => {
    store().seedConversations([conversation({ id: 'c1', unreadCount: 2 })])
    store().seedMessages('c1', [message({ id: 'm1' })])

    store().replaceMessage(edited(), 'me')
    store().replaceMessage(message({ id: 'm1', isDeleted: true, content: null }))

    expect(store().conversations.c1.unreadCount).toBe(2)
  })
})

describe('a later version arriving by another route', () => {
  it('is not undone by a page that was requested before the edit and arrives after it', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: 'edited', editCount: 1 })])

    store().seedMessages('c1', [message({ id: 'm1', content: 'original', editCount: 0 })])

    expect(store().messages.c1[0]).toMatchObject({ content: 'edited', editCount: 1 })
  })

  it('is applied from a page that is later than what is held', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: 'original' })])

    store().seedMessages('c1', [message({ id: 'm1', content: 'edited', editCount: 1 })])

    expect(store().messages.c1[0].content).toBe('edited')
  })

  it('stays deleted when an older page is loaded', () => {
    store().seedMessages('c1', [message({ id: 'm1', isDeleted: true, content: null })])

    store().seedMessages('c1', [message({ id: 'm1', content: 'original' })])

    expect(store().messages.c1[0].isDeleted).toBe(true)
  })

  it('is applied when a push of the message itself carries a later version than the one held', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: 'original' })])

    store().receiveMessage(message({ id: 'm1', content: 'edited', editCount: 1 }))

    expect(store().messages.c1[0].content).toBe('edited')
  })


  it('is applied together with what else the push changes (the sender’s read position)', () => {
    store().seedConversations([conversation({ id: 'c1' })])
    store().seedMembers('c1', [
      { userId: 'bob', userName: 'bob', displayName: 'Bob', avatarUrl: null, role: 'Member', lastReadAt: null },
    ])
    store().seedMessages('c1', [message({ id: 'm1', content: 'original' })])

    store().receiveMessage(message({ id: 'm1', content: 'edited', editCount: 1 }))

    expect(store().messages.c1[0].content).toBe('edited')
    expect(store().members.c1[0].lastReadAt).toBe('2026-10-11T10:00:00Z')
  })

  it('is not undone by the push of the original arriving late', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: 'edited', editCount: 1 })])

    store().receiveMessage(message({ id: 'm1', content: 'original', editCount: 0 }))

    expect(store().messages.c1[0].content).toBe('edited')
  })
})
