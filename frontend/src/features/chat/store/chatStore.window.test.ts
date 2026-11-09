import { beforeEach, describe, expect, it } from 'vitest'
import type { Conversation, Message } from '../types'
import { useChatStore } from './chatStore'

const store = () => useChatStore.getState()

function message(id: string, minute: number, overrides: Partial<Message> = {}): Message {
  return {
    id,
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: `text ${id}`,
    createdAt: `2026-10-11T10:${String(minute).padStart(2, '0')}:00Z`,
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    ...overrides,
  }
}

function conversation(overrides: Partial<Conversation> = {}): Conversation {
  return {
    id: 'c1',
    type: 'Direct',
    name: 'Bob',
    avatarUrl: null,
    updatedAt: '2026-10-11T10:30:00Z',
    lastMessage: { senderId: 'bob', content: 'last', createdAt: '2026-10-11T10:30:00Z', attachmentCount: 0 },
    unreadCount: 0,
    otherUserId: 'bob',
    isMuted: false,
    leftAt: null,
    keyEpoch: 0,
    keyRotationRequired: false,
    ...overrides,
  }
}

beforeEach(() => {
  store().reset()
  store().seedConversations([conversation()])
})

describe('opening a window of the history', () => {
  it('replaces what was held with the window, oldest first, and waits for the newest messages to join', () => {
    store().seedMessages('c1', [message('latest', 50)])

    store().showWindow('c1', [message('m3', 3), message('m1', 1), message('m2', 2)], { hasOlder: true, reachesNewest: false })

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m1', 'm2', 'm3'])
    expect(store().windows.c1).toEqual({ buffer: [], hasOlder: true })
  })

  it('is live at once when the window already reaches the newest message', () => {
    store().showWindow('c1', [message('m1', 1)], { hasOlder: false, reachesNewest: true })

    expect(store().windows.c1).toEqual({ buffer: null, hasOlder: false })
  })

  it('says whether older messages remain, and only changes when that changes', () => {
    store().showWindow('c1', [message('m1', 1)], { hasOlder: true, reachesNewest: false })
    const before = store().windows

    store().setWindowHasOlder('c1', true)
    expect(store().windows).toBe(before)

    store().setWindowHasOlder('c1', false)
    expect(store().windows.c1.hasOlder).toBe(false)

    store().setWindowHasOlder('unknown', false)
    expect(store().windows.unknown).toBeUndefined()
  })

  it('takes a page of older messages above the window by the ordinary merge', () => {
    store().showWindow('c1', [message('m10', 10), message('m11', 11)], { hasOlder: true, reachesNewest: false })

    store().seedMessages('c1', [message('m8', 8), message('m9', 9)])

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m8', 'm9', 'm10', 'm11'])
  })
})

describe('a message pushed while the window does not reach the newest message', () => {
  beforeEach(() => {
    store().showWindow('c1', [message('m1', 1), message('m2', 2)], { hasOlder: false, reachesNewest: false })
  })

  it('waits in the buffer and is not added after the window, where it would leave a hole', () => {
    store().receiveMessage(message('new', 40), 'me')

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m1', 'm2'])
    expect(store().windows.c1.buffer?.map((m) => m.id)).toEqual(['new'])
  })

  it('still counts as unread and moves the sidebar preview, which the buffer does not hide', () => {
    store().receiveMessage(message('new', 40, { content: 'fresh news' }), 'me')

    expect(store().conversations.c1.unreadCount).toBe(1)
    expect(store().conversations.c1.lastMessage?.content).toBe('fresh news')
    store().receiveMessage(message('newer', 59, { content: 'even fresher' }), 'me')
    expect(store().conversations.c1.lastMessage?.content).toBe('even fresher')
    expect(store().conversations.c1.unreadCount).toBe(2)
  })

  it('is buffered once however often it is pushed, and a message already held is not buffered', () => {
    store().receiveMessage(message('new', 40), 'me')
    store().receiveMessage(message('new', 40), 'me')
    store().receiveMessage(message('m1', 1), 'me')

    expect(store().windows.c1.buffer?.map((m) => m.id)).toEqual(['new'])
    expect(store().conversations.c1.unreadCount).toBe(1)
  })

  it('is appended as usual once the conversation is live again', () => {
    store().reattach('c1')

    store().receiveMessage(message('new', 40), 'me')

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m1', 'm2', 'new'])
  })

  it('follows an edit or a deletion of a message that is held', () => {
    store().replaceMessage(message('m2', 2, { content: 'edited', editCount: 1 }), 'me')

    expect(store().messages.c1.find((m) => m.id === 'm2')?.content).toBe('edited')
  })
})

describe('reaching the newest message', () => {
  it('joins what was pushed meanwhile to the window, in order, and goes live', () => {
    store().showWindow('c1', [message('m1', 1)], { hasOlder: false, reachesNewest: false })
    store().receiveMessage(message('b', 41), 'me')
    store().receiveMessage(message('a', 40), 'me')

    store().reattach('c1')

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m1', 'a', 'b'])
    expect(store().windows.c1.buffer).toBeNull()
  })

  it('keeps one copy of a message that the last page and the push both brought', () => {
    store().showWindow('c1', [message('m1', 1)], { hasOlder: false, reachesNewest: false })
    store().receiveMessage(message('a', 40), 'me')
    store().seedMessages('c1', [message('a', 40)])

    store().reattach('c1')

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m1', 'a'])
  })

  it('does nothing for a conversation that is not behind a window, or is already live', () => {
    store().seedMessages('c1', [message('m1', 1)])
    const before = store()

    store().reattach('c1')
    expect(store().messages).toBe(before.messages)

    store().showWindow('c1', [message('m1', 1)], { hasOlder: false, reachesNewest: true })
    const live = store().windows
    store().reattach('c1')
    expect(store().windows).toBe(live)
  })
})

describe('leaving the window', () => {
  it('drops what was held so the latest page can load again', () => {
    store().showWindow('c1', [message('m1', 1)], { hasOlder: true, reachesNewest: false })
    store().receiveMessage(message('b', 41), 'me')

    store().leaveWindow('c1')

    expect(store().messages.c1).toBeUndefined()
    expect(store().windows.c1).toBeUndefined()
  })

  it('does nothing for a conversation with no window, and keeps what it holds', () => {
    store().seedMessages('c1', [message('m1', 1)])
    const before = store().messages

    store().leaveWindow('c1')

    expect(store().messages).toBe(before)
  })

  it('is part of clearing a conversation, and of being added back to a group after leaving it', () => {
    store().showWindow('c1', [message('m1', 1)], { hasOlder: true, reachesNewest: false })
    store().clearMessages('c1')
    expect(store().windows.c1).toBeUndefined()
    expect(store().messages.c1).toBeUndefined()

    store().seedConversations([conversation({ id: 'g1', type: 'Group', leftAt: '2026-10-11T10:00:00Z' })])
    store().showWindow('g1', [message('g', 1, { conversationId: 'g1' })], { hasOlder: false, reachesNewest: true })
    store().addConversation(conversation({ id: 'g1', type: 'Group', leftAt: null, updatedAt: '2026-10-11T11:00:00Z' }))
    expect(store().windows.g1).toBeUndefined()
  })
})
