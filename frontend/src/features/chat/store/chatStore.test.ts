import { beforeEach, describe, expect, it } from 'vitest'
import { sortedConversations, useChatStore } from './chatStore'
import type { Attachment, Conversation, Member, Message, PendingMessage } from '../types'

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
    leftAt: null,
    keyEpoch: 0,
    keyRotationRequired: false,
    ...overrides,
  }
}

function attachment(id: string, overrides: Partial<Attachment> = {}): Attachment {
  return {
    id,
    type: 'Image',
    fileName: `${id}.png`,
    contentType: 'image/png',
    sizeBytes: 1000,
    url: `https://files.test/${id}?signature=old`,
    ...overrides,
  }
}

function pending(clientMessageId: string, overrides: Partial<PendingMessage> = {}): PendingMessage {
  return {
    clientMessageId,
    conversationId: 'c1',
    content: `pending ${clientMessageId}`,
    attachments: [],
    createdAt: '2026-10-11T10:00:00Z',
    status: 'sending',
    error: null,
    ...overrides,
  }
}

const ids = (messages: Message[] | undefined) => (messages ?? []).map((m) => m.id)

beforeEach(() => {
  store().reset()
})

// ##### Loading history #####

describe('seedMessages', () => {
  it('keeps history oldest first however the server order arrives', () => {
    store().seedMessages('c1', [
      message({ id: 'b', createdAt: '2026-10-11T10:00:02Z' }),
      message({ id: 'a', createdAt: '2026-10-11T10:00:01Z' }),
      message({ id: 'c', createdAt: '2026-10-11T10:00:03Z' }),
    ])

    expect(ids(store().messages.c1)).toEqual(['a', 'b', 'c'])
  })

  it('keeps a message that was pushed while the history request was in flight', () => {
    store().receiveMessage(message({ id: 'pushed', createdAt: '2026-10-11T10:00:09Z' }))

    store().seedMessages('c1', [message({ id: 'old', createdAt: '2026-10-11T10:00:01Z' })])

    expect(ids(store().messages.c1)).toEqual(['old', 'pushed'])
  })

  it('does not duplicate a message that is both pushed and in the history', () => {
    store().receiveMessage(message({ id: 'both', createdAt: '2026-10-11T10:00:05Z' }))

    store().seedMessages('c1', [
      message({ id: 'before', createdAt: '2026-10-11T10:00:01Z' }),
      message({ id: 'both', createdAt: '2026-10-11T10:00:05Z' }),
    ])

    expect(ids(store().messages.c1)).toEqual(['before', 'both'])
  })

  // The send reached the server and only its answer was lost, so the tab shows the message as failed. When
  // the history (a reload, or the catch-up after a reconnect) returns the stored message with the id the tab
  // invented, that message is delivered and the failed copy must not stay next to it.
  it('removes the unconfirmed copy of a message that the history returns with its client id', () => {
    store().addPending(pending('p1', { status: 'failed', error: 'Network Error' }))

    store().seedMessages('c1', [message({ id: 'stored', clientMessageId: 'p1' })])

    expect(store().pending.c1).toEqual([])
    expect(ids(store().messages.c1)).toEqual(['stored'])
  })

  it('keeps the unconfirmed messages that nothing loaded matches', () => {
    store().addPending(pending('p1'))
    store().addPending(pending('p2'))

    store().seedMessages('c1', [message({ id: 'stored', clientMessageId: 'p1' }), message({ id: 'plain', clientMessageId: null })])

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p2'])
  })

  it('leaves the unconfirmed messages of another conversation alone', () => {
    store().addPending(pending('p1', { conversationId: 'c2' }))

    store().seedMessages('c1', [message({ id: 'stored', clientMessageId: 'p1' })])

    expect(store().pending.c2.map((p) => p.clientMessageId)).toEqual(['p1'])
  })

  it('does not touch the pending list when the history confirms nothing', () => {
    store().addPending(pending('p1'))
    const before = store().pending

    store().seedMessages('c1', [message({ id: 'other', clientMessageId: null })])

    expect(store().pending).toBe(before)
  })

  it('keeps the client id of a message when a history load returns it without one', () => {
    store().receiveMessage(message({ id: 'm', clientMessageId: 'p1' }))

    store().seedMessages('c1', [message({ id: 'm', clientMessageId: null })])

    expect(store().messages.c1[0].clientMessageId).toBe('p1')
  })

  it('lets the server copy win when a message changed (for example it was deleted)', () => {
    store().receiveMessage(message({ id: 'm', content: 'secret' }))

    store().seedMessages('c1', [message({ id: 'm', content: null, isDeleted: true })])

    expect(store().messages.c1[0].content).toBeNull()
  })

  it('does not touch other conversations', () => {
    store().seedMessages('c2', [message({ id: 'other', conversationId: 'c2' })])

    store().seedMessages('c1', [message({ id: 'mine' })])

    expect(ids(store().messages.c2)).toEqual(['other'])
  })

  it('leaves unconfirmed messages alone', () => {
    store().addPending(pending('p1'))

    store().seedMessages('c1', [message({ id: 'a' })])

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p1'])
  })
})

describe('clearMessages', () => {
  it('forgets the confirmed messages of one conversation and no other', () => {
    store().seedMessages('c1', [message({ id: 'a' })])
    store().seedMessages('c2', [message({ id: 'b', conversationId: 'c2' })])

    store().clearMessages('c1')

    expect(store().messages.c1).toBeUndefined()
    expect(ids(store().messages.c2)).toEqual(['b'])
  })

  it('keeps the unconfirmed messages of that conversation', () => {
    store().seedMessages('c1', [message({ id: 'a' })])
    store().addPending(pending('p1'))

    store().clearMessages('c1')

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p1'])
  })

  it('does not tell anyone the state changed when there was nothing to clear', () => {
    const before = store()

    store().clearMessages('never-loaded')

    expect(store()).toBe(before)
  })
})

// ##### Receiving a message #####

describe('receiveMessage', () => {
  it('adds a message to its conversation', () => {
    store().receiveMessage(message({ id: 'a' }))

    expect(ids(store().messages.c1)).toEqual(['a'])
  })

  it('places a message by time even when it arrives out of order', () => {
    store().receiveMessage(message({ id: 'late', createdAt: '2026-10-11T10:00:09Z' }))
    store().receiveMessage(message({ id: 'early', createdAt: '2026-10-11T10:00:01Z' }))
    store().receiveMessage(message({ id: 'middle', createdAt: '2026-10-11T10:00:05Z' }))

    expect(ids(store().messages.c1)).toEqual(['early', 'middle', 'late'])
  })

  it('orders timestamps with different fraction lengths by value', () => {
    store().receiveMessage(message({ id: 'b', createdAt: '2026-10-11T10:00:00.494153Z' }))
    store().receiveMessage(message({ id: 'a', createdAt: '2026-10-11T10:00:00.49Z' }))

    expect(ids(store().messages.c1)).toEqual(['a', 'b'])
  })

  it('orders messages with the same timestamp the same way every time', () => {
    store().receiveMessage(message({ id: 'z', createdAt: '2026-10-11T10:00:00Z' }))
    store().receiveMessage(message({ id: 'a', createdAt: '2026-10-11T10:00:00Z' }))
    const first = ids(store().messages.c1)

    store().reset()
    store().receiveMessage(message({ id: 'a', createdAt: '2026-10-11T10:00:00Z' }))
    store().receiveMessage(message({ id: 'z', createdAt: '2026-10-11T10:00:00Z' }))

    expect(ids(store().messages.c1)).toEqual(first)
  })

  it('ignores a message it already has, without telling anyone the state changed', () => {
    store().receiveMessage(message({ id: 'a' }))
    let notifications = 0
    const unsubscribe = useChatStore.subscribe(() => {
      notifications++
    })

    store().receiveMessage(message({ id: 'a' }))
    unsubscribe()

    expect(ids(store().messages.c1)).toEqual(['a'])
    expect(notifications).toBe(0)
  })

  it('keeps conversations apart', () => {
    store().receiveMessage(message({ id: 'a', conversationId: 'c1' }))
    store().receiveMessage(message({ id: 'b', conversationId: 'c2' }))

    expect(ids(store().messages.c1)).toEqual(['a'])
    expect(ids(store().messages.c2)).toEqual(['b'])
  })
})

// ##### Reconciling your own messages #####

describe('confirming a sent message', () => {
  it('replaces the unconfirmed copy when the server response arrives', () => {
    store().addPending(pending('p1'))

    store().receiveMessage(message({ id: 'real', senderId: 'me', clientMessageId: 'p1' }))

    expect(store().pending.c1).toEqual([])
    expect(ids(store().messages.c1)).toEqual(['real'])
  })

  it('shows the message once when the push and the response both arrive, push first', () => {
    store().addPending(pending('p1'))
    const confirmed = message({ id: 'real', senderId: 'me', clientMessageId: 'p1' })

    store().receiveMessage(confirmed)
    store().receiveMessage(confirmed)

    expect(ids(store().messages.c1)).toEqual(['real'])
    expect(store().pending.c1).toEqual([])
  })

  it('also confirms a message that was already marked as failed', () => {
    store().addPending(pending('p1'))
    store().failPending('c1', 'p1', 'Network Error')

    store().receiveMessage(message({ id: 'real', clientMessageId: 'p1' }))

    expect(store().pending.c1).toEqual([])
  })

  it('leaves other unconfirmed messages alone', () => {
    store().addPending(pending('p1'))
    store().addPending(pending('p2'))

    store().receiveMessage(message({ id: 'real', clientMessageId: 'p1' }))

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p2'])
  })

  it('does not confuse another sender whose text matches an unconfirmed message', () => {
    store().addPending(pending('p1', { content: 'ok' }))

    store().receiveMessage(message({ id: 'theirs', content: 'ok', clientMessageId: null }))

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p1'])
  })

  it('does not confirm a message in another conversation', () => {
    store().addPending(pending('p1', { conversationId: 'c1' }))

    store().receiveMessage(message({ id: 'x', conversationId: 'c2', clientMessageId: 'p1' }))

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p1'])
  })
})

describe('unconfirmed messages', () => {
  it('start as sending, can fail with a reason, and can be retried', () => {
    store().addPending(pending('p1'))
    expect(store().pending.c1[0].status).toBe('sending')

    store().failPending('c1', 'p1', 'You are not a member of this conversation.')
    expect(store().pending.c1[0]).toMatchObject({
      status: 'failed',
      error: 'You are not a member of this conversation.',
    })

    store().retryPending('c1', 'p1')
    expect(store().pending.c1[0]).toMatchObject({ status: 'sending', error: null })
  })

  it('can be dismissed', () => {
    store().addPending(pending('p1'))
    store().addPending(pending('p2'))

    store().removePending('c1', 'p1')

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p2'])
  })

  it('keeps the order they were sent in', () => {
    store().addPending(pending('p1'))
    store().addPending(pending('p2'))
    store().addPending(pending('p3'))

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p1', 'p2', 'p3'])
  })

  it('ignore an unknown id', () => {
    store().addPending(pending('p1'))

    store().failPending('c1', 'nope', 'x')
    store().retryPending('c1', 'nope')
    store().removePending('c1', 'nope')

    expect(store().pending.c1).toHaveLength(1)
    expect(store().pending.c1[0].status).toBe('sending')
  })
})

// ##### The sidebar #####

describe('conversations', () => {
  it('are listed with the most recently active first', () => {
    store().seedConversations([
      conversation({ id: 'old', updatedAt: '2026-10-11T08:00:00Z' }),
      conversation({ id: 'new', updatedAt: '2026-10-11T09:30:00Z' }),
      conversation({ id: 'mid', updatedAt: '2026-10-11T09:00:00Z' }),
    ])

    expect(sortedConversations(store().conversations).map((c) => c.id)).toEqual(['new', 'mid', 'old'])
  })

  it('move to the top and show the new text when a message arrives', () => {
    store().seedConversations([
      conversation({ id: 'c1', updatedAt: '2026-10-11T08:00:00Z' }),
      conversation({ id: 'c2', updatedAt: '2026-10-11T09:00:00Z' }),
    ])

    store().receiveMessage(
      message({ id: 'm', conversationId: 'c1', senderId: 'bob', content: 'hello', createdAt: '2026-10-11T10:00:00Z' }),
    )

    const list = sortedConversations(store().conversations)
    expect(list.map((c) => c.id)).toEqual(['c1', 'c2'])
    expect(list[0].updatedAt).toBe('2026-10-11T10:00:00Z')
    expect(list[0].lastMessage).toEqual({
      senderId: 'bob',
      content: 'hello',
      unreadable: null,
      createdAt: '2026-10-11T10:00:00Z',
      attachmentCount: 0,
      sealed: null,
    })
  })

  it('say how many files the newest message carries, so a message of only files can be previewed', () => {
    store().seedConversations([conversation({ id: 'c1' })])

    store().receiveMessage(
      message({ id: 'm', content: '', attachments: [attachment('a'), attachment('b')], createdAt: '2026-10-11T10:00:00Z' }),
    )

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: '', attachmentCount: 2 })
  })

  it('keep the newest preview when an older message arrives late', () => {
    store().seedConversations([conversation({ id: 'c1' })])
    store().receiveMessage(message({ id: 'new', content: 'newest', createdAt: '2026-10-11T10:00:09Z' }))

    store().receiveMessage(message({ id: 'old', content: 'older', createdAt: '2026-10-11T10:00:01Z' }))

    expect(store().conversations.c1.lastMessage?.content).toBe('newest')
    expect(store().conversations.c1.updatedAt).toBe('2026-10-11T10:00:09Z')
  })

  it('are not invented from a message for a conversation the sidebar does not know', () => {
    store().receiveMessage(message({ id: 'm', conversationId: 'mystery' }))

    expect(store().conversations.mystery).toBeUndefined()
    expect(ids(store().messages.mystery)).toEqual(['m'])
  })

  it('can be added when the server announces a new one', () => {
    store().addConversation(conversation({ id: 'fresh' }))

    expect(store().conversations.fresh.name).toBe('Conversation fresh')
  })

  it('are not duplicated when announced twice (response and push)', () => {
    store().addConversation(conversation({ id: 'fresh' }))
    store().addConversation(conversation({ id: 'fresh' }))

    expect(Object.keys(store().conversations)).toEqual(['fresh'])
  })

  it('keep a newer local state when an older copy arrives', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: '2026-10-11T09:00:00Z' })])
    store().receiveMessage(message({ id: 'm', createdAt: '2026-10-11T10:00:00Z' }))

    store().seedConversations([conversation({ id: 'c1', updatedAt: '2026-10-11T09:00:00Z' })])
    store().addConversation(conversation({ id: 'c1', updatedAt: '2026-10-11T09:00:00Z' }))

    expect(store().conversations.c1.updatedAt).toBe('2026-10-11T10:00:00Z')
  })

  it('take a newer server copy over an older local one', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: '2026-10-11T09:00:00Z' })])

    store().seedConversations([
      conversation({
        id: 'c1',
        updatedAt: '2026-10-11T11:00:00Z',
        lastMessage: { senderId: 'x', content: 'from another device', createdAt: '2026-10-11T11:00:00Z', attachmentCount: 0 },
      }),
    ])

    expect(store().conversations.c1.lastMessage?.content).toBe('from another device')
  })

  it('keep one that was announced while the list request was in flight', () => {
    store().addConversation(conversation({ id: 'announced' }))

    store().seedConversations([conversation({ id: 'listed' })])

    expect(Object.keys(store().conversations).sort()).toEqual(['announced', 'listed'])
  })
})

// ##### Typing #####

describe('typing', () => {
  it('shows who is typing in a conversation', () => {
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)

    expect(store().typing.c1).toEqual([{ userId: 'bob', displayName: 'Bob' }])
  })

  it('lists several people and each person once', () => {
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)
    store().setTyping('c1', { userId: 'carol', displayName: 'Carol' }, true)
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)

    expect(store().typing.c1.map((u) => u.userId)).toEqual(['bob', 'carol'])
  })

  it('removes a person who stops', () => {
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)
    store().setTyping('c1', { userId: 'carol', displayName: 'Carol' }, true)

    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, false)

    expect(store().typing.c1.map((u) => u.userId)).toEqual(['carol'])
  })

  it('keeps conversations apart', () => {
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)

    expect(store().typing.c2).toBeUndefined()
  })

  it('stops showing someone who sends a message', () => {
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)

    store().receiveMessage(message({ id: 'm', senderId: 'bob' }))

    expect(store().typing.c1).toEqual([])
  })

  it('keeps showing others when one person sends', () => {
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)
    store().setTyping('c1', { userId: 'carol', displayName: 'Carol' }, true)

    store().receiveMessage(message({ id: 'm', senderId: 'bob' }))

    expect(store().typing.c1.map((u) => u.userId)).toEqual(['carol'])
  })

  it('does nothing when someone who is not typing stops', () => {
    let notifications = 0
    const unsubscribe = useChatStore.subscribe(() => {
      notifications++
    })

    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, false)
    unsubscribe()

    expect(notifications).toBe(0)
  })
})

// ##### Connection and sign-out #####

describe('connection and reset', () => {
  it('records the connection status', () => {
    expect(store().connection).toBe('connecting')

    store().setConnection('connected')

    expect(store().connection).toBe('connected')
  })

  it('reset forgets everything, so one user never sees the previous user\'s chats', () => {
    store().setConnection('connected')
    store().seedConversations([conversation({ id: 'c1' })])
    store().receiveMessage(message({ id: 'm' }))
    store().addPending(pending('p1'))
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)

    store().reset()

    expect(store().connection).toBe('connecting')
    expect(store().conversations).toEqual({})
    expect(store().messages).toEqual({})
    expect(store().pending).toEqual({})
    expect(store().typing).toEqual({})
    expect(store().members).toEqual({})
  })
})

// ##### Read state #####

function member(userId: string, lastReadAt: string | null): Member {
  return { userId, userName: userId, displayName: userId, avatarUrl: null, role: 'Member', lastReadAt }
}

const unread = (id = 'c1') => store().conversations[id].unreadCount
const readBy = (userId: string, id = 'c1') => store().members[id]?.find((m) => m.userId === userId)?.lastReadAt

describe('unread counts', () => {
  beforeEach(() => {
    store().seedConversations([
      conversation({
        id: 'c1',
        unreadCount: 2,
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T10:00:00Z', attachmentCount: 0 },
      }),
    ])
  })

  it('go up by one for each new message from someone else', () => {
    store().receiveMessage(message({ id: 'a', createdAt: '2026-10-11T10:00:01Z' }), 'me')
    store().receiveMessage(message({ id: 'b', createdAt: '2026-10-11T10:00:02Z' }), 'me')

    expect(unread()).toBe(4)
  })

  it('do not go up twice for a message that arrives by push and again in a history load', () => {
    const arrival = message({ id: 'a', createdAt: '2026-10-11T10:00:01Z' })

    store().receiveMessage(arrival, 'me')
    store().receiveMessage(arrival, 'me')

    expect(unread()).toBe(3)
  })

  it('are not changed by a history load, which is not news', () => {
    store().seedMessages('c1', [message({ id: 'old', createdAt: '2026-10-11T09:00:00Z' })])

    expect(unread()).toBe(2)
  })

  it('are left alone when the caller does not say who the signed-in user is', () => {
    store().receiveMessage(message({ id: 'a', createdAt: '2026-10-11T10:00:01Z' }))

    expect(unread()).toBe(2)
  })

  it('clear when you send a message, because sending counts as reading', () => {
    store().receiveMessage(message({ id: 'mine', senderId: 'me', createdAt: '2026-10-11T10:00:05Z' }), 'me')

    expect(unread()).toBe(0)
  })

  it('keep only the messages from others that are after yours', () => {
    store().receiveMessage(message({ id: 'later', senderId: 'bob', createdAt: '2026-10-11T10:00:09Z' }), 'me')
    expect(unread()).toBe(3)

    store().receiveMessage(message({ id: 'mine', senderId: 'me', createdAt: '2026-10-11T10:00:05Z' }), 'me')

    // Only the message from Bob at :09 is after yours.
    expect(unread()).toBe(1)
  })

  it('are not invented for a conversation the sidebar does not know', () => {
    store().receiveMessage(message({ id: 'x', conversationId: 'unknown' }), 'me')

    expect(store().conversations.unknown).toBeUndefined()
  })

  it('come from the server when the list is loaded again', () => {
    store().seedConversations([
      conversation({
        id: 'c1',
        unreadCount: 0,
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T10:00:00Z', attachmentCount: 0 },
      }),
    ])

    expect(unread()).toBe(0)
  })
})

describe('applying a read', () => {
  beforeEach(() => {
    store().seedConversations([
      conversation({
        id: 'c1',
        unreadCount: 3,
        updatedAt: '2026-10-11T10:00:03Z',
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T10:00:03Z', attachmentCount: 0 },
      }),
    ])
    store().seedMessages('c1', [
      message({ id: 'm1', createdAt: '2026-10-11T10:00:01Z' }),
      message({ id: 'm2', createdAt: '2026-10-11T10:00:02Z' }),
      message({ id: 'm3', createdAt: '2026-10-11T10:00:03Z' }),
    ])
    store().seedMembers('c1', [member('me', null), member('bob', null)])
  })

  it('clears your own count when you read up to the newest message, whichever tab did it', () => {
    store().applyRead('c1', 'me', '2026-10-11T10:00:03Z', 'me')

    expect(unread()).toBe(0)
  })

  it('leaves only the messages after the read position unread when you read partway', () => {
    store().applyRead('c1', 'me', '2026-10-11T10:00:01Z', 'me')

    expect(unread()).toBe(2)
  })

  it('does not count your own messages as unread after a partial read', () => {
    store().receiveMessage(message({ id: 'm4', senderId: 'bob', createdAt: '2026-10-11T10:00:04Z' }), 'me')
    store().seedMessages('c1', [message({ id: 'own', senderId: 'me', createdAt: '2026-10-11T10:00:02.5Z' })])

    store().applyRead('c1', 'me', '2026-10-11T10:00:01Z', 'me')

    // m2, m3 and m4 are Bob's and after :01; "own" is yours.
    expect(unread()).toBe(3)
  })

  it('never raises the count', () => {
    store().applyRead('c1', 'me', '2026-10-11T10:00:00Z', 'me')

    expect(unread()).toBe(3)
  })

  // Two tabs, or pushes arriving out of order: a read of an older message can reach this tab after
  // a read of a newer one. It must not make the messages read in between unread again.
  it('does not raise the count when an older read arrives after a newer one', () => {
    store().applyRead('c1', 'me', '2026-10-11T10:00:02Z', 'me')
    expect(unread()).toBe(1)

    store().applyRead('c1', 'me', '2026-10-11T10:00:01Z', 'me')

    expect(unread()).toBe(1)
  })

  it('counts nothing when the conversation has no unread messages to begin with', () => {
    store().applyRead('c1', 'me', '2026-10-11T10:00:03Z', 'me')
    const before = store()

    store().applyRead('c1', 'me', '2026-10-11T10:00:01Z', 'me')

    expect(unread()).toBe(0)
    expect(store().conversations).toBe(before.conversations)
  })

  it('records where another member has read to', () => {
    store().applyRead('c1', 'bob', '2026-10-11T10:00:02Z', 'me')

    expect(readBy('bob')).toBe('2026-10-11T10:00:02Z')
  })

  it('does not change your count when someone else reads', () => {
    store().applyRead('c1', 'bob', '2026-10-11T10:00:03Z', 'me')

    expect(unread()).toBe(3)
  })

  it('never moves a member\'s read position backwards', () => {
    store().applyRead('c1', 'bob', '2026-10-11T10:00:03Z', 'me')

    store().applyRead('c1', 'bob', '2026-10-11T10:00:01Z', 'me')

    expect(readBy('bob')).toBe('2026-10-11T10:00:03Z')
  })

  it('compares positions by value, not as text', () => {
    store().applyRead('c1', 'bob', '2026-10-11T10:00:03.5Z', 'me')

    store().applyRead('c1', 'bob', '2026-10-11T10:00:03Z', 'me')

    expect(readBy('bob')).toBe('2026-10-11T10:00:03.5Z')
  })

  it('ignores a member it does not know and a conversation it has not loaded', () => {
    const before = store()

    store().applyRead('c1', 'stranger', '2026-10-11T10:00:03Z', 'me')
    store().applyRead('nowhere', 'bob', '2026-10-11T10:00:03Z', 'me')

    expect(store()).toBe(before)
  })

  it('does not tell anyone anything when it changes nothing', () => {
    store().applyRead('c1', 'bob', '2026-10-11T10:00:02Z', 'me')
    const before = store()

    store().applyRead('c1', 'bob', '2026-10-11T10:00:02Z', 'me')

    expect(store()).toBe(before)
  })
})

describe('members', () => {
  it('are stored per conversation', () => {
    store().seedMembers('c1', [member('me', null), member('bob', '2026-10-11T10:00:01Z')])

    expect(store().members.c1.map((m) => m.userId)).toEqual(['me', 'bob'])
  })

  it('keep a read position that arrived by push while the list was being loaded', () => {
    store().seedMembers('c1', [member('bob', null)])
    store().applyRead('c1', 'bob', '2026-10-11T10:00:05Z', 'me')

    store().seedMembers('c1', [member('bob', '2026-10-11T10:00:02Z')])

    expect(readBy('bob')).toBe('2026-10-11T10:00:05Z')
  })

  it('take a newer position from the server over an older one held', () => {
    store().seedMembers('c1', [member('bob', '2026-10-11T10:00:02Z')])

    store().seedMembers('c1', [member('bob', '2026-10-11T10:00:09Z')])

    expect(readBy('bob')).toBe('2026-10-11T10:00:09Z')
  })

  it('follow the server for who is in the conversation', () => {
    store().seedMembers('c1', [member('me', null), member('bob', null)])

    store().seedMembers('c1', [member('me', null), member('cat', null)])

    expect(store().members.c1.map((m) => m.userId)).toEqual(['me', 'cat'])
  })

  it('treat a message from a member as proof that they have read up to it', () => {
    store().seedMembers('c1', [member('me', null), member('bob', null)])

    store().receiveMessage(message({ id: 'a', senderId: 'bob', createdAt: '2026-10-11T10:00:07Z' }))

    expect(readBy('bob')).toBe('2026-10-11T10:00:07Z')
    expect(readBy('me')).toBeNull()
  })
})

describe('attachment links', () => {
  const linkOf = (messageId: string, attachmentId: string) =>
    store().messages.c1.find((m) => m.id === messageId)?.attachments.find((a) => a.id === attachmentId)?.url

  beforeEach(() => {
    store().seedMessages('c1', [
      message({ id: 'm1', attachments: [attachment('a'), attachment('b')] }),
      message({ id: 'm2', createdAt: '2026-10-11T10:00:01Z', attachments: [attachment('c')] }),
    ])
  })

  it('replaces the link of one attachment', () => {
    store().setAttachmentUrl('c1', 'm1', 'a', 'https://files.test/a?signature=new')

    expect(linkOf('m1', 'a')).toBe('https://files.test/a?signature=new')
  })

  it('leaves the other attachments and messages alone', () => {
    const before = store().messages.c1

    store().setAttachmentUrl('c1', 'm1', 'a', 'https://files.test/a?signature=new')

    expect(linkOf('m1', 'b')).toBe('https://files.test/b?signature=old')
    expect(linkOf('m2', 'c')).toBe('https://files.test/c?signature=old')
    expect(store().messages.c1[1]).toBe(before[1])
  })

  it('does nothing, and says nothing changed, for a message or attachment that is not there', () => {
    const before = store().messages

    store().setAttachmentUrl('c1', 'nope', 'a', 'x')
    store().setAttachmentUrl('c1', 'm1', 'nope', 'x')
    store().setAttachmentUrl('other', 'm1', 'a', 'x')

    expect(store().messages).toBe(before)
  })

  it('does nothing when the link is the one already held', () => {
    const before = store().messages

    store().setAttachmentUrl('c1', 'm1', 'a', 'https://files.test/a?signature=old')

    expect(store().messages).toBe(before)
  })
})

describe('muting a conversation', () => {
  it('records the choice on that conversation only', () => {
    store().seedConversations([conversation({ id: 'c1' }), conversation({ id: 'c2' })])

    store().setMuted('c1', true)

    expect(store().conversations.c1.isMuted).toBe(true)
    expect(store().conversations.c2.isMuted).toBe(false)
  })

  it('can be undone', () => {
    store().seedConversations([conversation({ id: 'c1', isMuted: true })])

    store().setMuted('c1', false)

    expect(store().conversations.c1.isMuted).toBe(false)
  })

  it('leaves the unread count and the preview alone', () => {
    store().seedConversations([
      conversation({
        id: 'c1',
        unreadCount: 4,
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T10:00:00Z', attachmentCount: 0 },
      }),
    ])
    const before = store().conversations.c1

    store().setMuted('c1', true)

    expect(store().conversations.c1).toEqual({ ...before, isMuted: true })
  })

  it('says nothing changed when the choice is the one already held', () => {
    store().seedConversations([conversation({ id: 'c1', isMuted: true })])
    const before = store().conversations

    store().setMuted('c1', true)

    expect(store().conversations).toBe(before)
  })

  it('ignores a conversation the sidebar does not know', () => {
    const before = store().conversations

    store().setMuted('nope', true)

    expect(store().conversations).toBe(before)
  })

  it('is not undone by a message arriving', () => {
    store().seedConversations([conversation({ id: 'c1', isMuted: true })])

    store().receiveMessage(message({ id: 'm1', conversationId: 'c1', senderId: 'bob' }), 'me')

    expect(store().conversations.c1.isMuted).toBe(true)
    expect(store().conversations.c1.unreadCount).toBe(1)
  })
})

describe('the keys of a conversation', () => {
  it('take the epoch and the need to replace it that the server pushed', () => {
    store().seedConversations([conversation({ id: 'c1' }), conversation({ id: 'c2' })])

    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 1, keyRotationRequired: false })
    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 1, keyRotationRequired: true })

    expect(store().conversations.c1).toMatchObject({ keyEpoch: 1, keyRotationRequired: true })
    expect(store().conversations.c2).toMatchObject({ keyEpoch: 0, keyRotationRequired: false })
  })

  it('are not lowered by a push that is older than what is held', () => {
    store().seedConversations([conversation({ id: 'c1', keyEpoch: 4 })])

    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 3, keyRotationRequired: true })

    expect(store().conversations.c1).toMatchObject({ keyEpoch: 4, keyRotationRequired: false })
  })

  it('keep the need to replace the epoch when a late push for the same epoch says it was not needed', () => {
    store().seedConversations([conversation({ id: 'c1', keyEpoch: 2, keyRotationRequired: true })])

    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 2, keyRotationRequired: false })

    expect(store().conversations.c1.keyRotationRequired).toBe(true)
  })

  it('turn the need off when a newer epoch arrives', () => {
    store().seedConversations([conversation({ id: 'c1', keyEpoch: 2, keyRotationRequired: true })])

    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 3, keyRotationRequired: false })

    expect(store().conversations.c1).toMatchObject({ keyEpoch: 3, keyRotationRequired: false })
  })

  it('take a newer epoch that already needs replacing', () => {
    store().seedConversations([conversation({ id: 'c1', keyEpoch: 2 })])

    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 3, keyRotationRequired: true })

    expect(store().conversations.c1).toMatchObject({ keyEpoch: 3, keyRotationRequired: true })
  })

  it('say nothing changed when the push repeats what is held', () => {
    store().seedConversations([conversation({ id: 'c1', keyEpoch: 2, keyRotationRequired: true })])
    const before = store().conversations

    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 2, keyRotationRequired: true })

    expect(store().conversations).toBe(before)
  })

  it('ignore a conversation the sidebar does not know', () => {
    const before = store().conversations

    store().applyKeyChange({ conversationId: 'nope', keyEpoch: 1, keyRotationRequired: false })

    expect(store().conversations).toBe(before)
  })

  it('leave the unread count and the preview alone', () => {
    store().seedConversations([conversation({ id: 'c1', unreadCount: 4 })])
    const before = store().conversations.c1

    store().applyKeyChange({ conversationId: 'c1', keyEpoch: 2, keyRotationRequired: true })

    expect(store().conversations.c1).toEqual({ ...before, keyEpoch: 2, keyRotationRequired: true })
  })
})

describe('the keys of a conversation when copies of it are merged', () => {
  const OLD = '2026-10-11T09:00:00Z'
  const NEW = '2026-10-11T10:00:00Z'

  it('take a newer epoch from a list that is older by activity than the copy held', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: NEW, keyEpoch: 1, unreadCount: 4 })])

    store().seedConversations([conversation({ id: 'c1', updatedAt: OLD, keyEpoch: 3, keyRotationRequired: true })])

    expect(store().conversations.c1).toMatchObject({ updatedAt: NEW, unreadCount: 4, keyEpoch: 3, keyRotationRequired: true })
  })

  it('keep a newer epoch that the copy held already, when the list is fresher by activity', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: OLD, keyEpoch: 5 })])

    store().seedConversations([conversation({ id: 'c1', updatedAt: NEW, keyEpoch: 4, unreadCount: 2 })])

    expect(store().conversations.c1).toMatchObject({ updatedAt: NEW, unreadCount: 2, keyEpoch: 5 })
  })

  it('keep the need to replace the epoch that the copy held when the list does not know it yet', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: OLD, keyEpoch: 2, keyRotationRequired: true })])

    store().seedConversations([conversation({ id: 'c1', updatedAt: NEW, keyEpoch: 2, keyRotationRequired: false })])

    expect(store().conversations.c1.keyRotationRequired).toBe(true)
  })

  it('turn the need off when the list carries a newer epoch', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: NEW, keyEpoch: 2, keyRotationRequired: true })])

    store().seedConversations([conversation({ id: 'c1', updatedAt: OLD, keyEpoch: 3, keyRotationRequired: false })])

    expect(store().conversations.c1).toMatchObject({ keyEpoch: 3, keyRotationRequired: false })
  })

  it('are merged the same way when a conversation is added that is already held', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: NEW, keyEpoch: 1 })])

    store().addConversation(conversation({ id: 'c1', updatedAt: OLD, keyEpoch: 2 }))

    expect(store().conversations.c1).toMatchObject({ updatedAt: NEW, keyEpoch: 2 })
  })

  it('say nothing changed when both copies agree', () => {
    store().seedConversations([conversation({ id: 'c1', updatedAt: NEW, keyEpoch: 2, keyRotationRequired: true })])
    const before = store().conversations.c1

    store().seedConversations([conversation({ id: 'c1', updatedAt: NEW, keyEpoch: 2, keyRotationRequired: true })])

    expect(store().conversations.c1).toStrictEqual(before)
  })
})

// ##### Opening what could not be opened #####

describe('applyOpened', () => {
  it('puts the text on a message that could not be opened', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: null, unreadable: 'noKey' })])

    store().applyOpened('c1', 'm1', 0, { content: 'now readable', unreadable: null })

    expect(store().messages.c1[0]).toMatchObject({ content: 'now readable', unreadable: null })
  })

  it('records why a message is still not shown', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: null, unreadable: 'noKey' })])

    store().applyOpened('c1', 'm1', 0, { content: null, unreadable: 'senderChanged' })

    expect(store().messages.c1[0]).toMatchObject({ content: null, unreadable: 'senderChanged' })
  })

  it('ignores a result for an older version than the one held', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: null, unreadable: 'noKey', editCount: 2 })])

    store().applyOpened('c1', 'm1', 1, { content: 'old text', unreadable: null })

    expect(store().messages.c1[0]).toMatchObject({ content: null, unreadable: 'noKey', editCount: 2 })
  })

  it('ignores a result for a message that was deleted, or that is not held', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: null, isDeleted: true })])

    store().applyOpened('c1', 'm1', 0, { content: 'text', unreadable: null })
    store().applyOpened('c1', 'nope', 0, { content: 'text', unreadable: null })
    store().applyOpened('other', 'm1', 0, { content: 'text', unreadable: null })

    expect(store().messages.c1[0].content).toBeNull()
  })

  it('changes nothing when the message already shows that', () => {
    store().seedMessages('c1', [message({ id: 'm1', content: 'same', unreadable: null })])
    const before = store().messages

    store().applyOpened('c1', 'm1', 0, { content: 'same', unreadable: null })

    expect(store().messages).toBe(before)
  })

  it('shows the text in the list too, when the message is the last one', () => {
    store().seedConversations([conversation({ id: 'c1' })])
    store().receiveMessage(message({ id: 'm1', content: null, unreadable: 'noKey', createdAt: '2026-10-11T10:00:00Z' }))
    expect(store().conversations.c1.lastMessage).toMatchObject({ content: null, unreadable: 'noKey' })

    store().applyOpened('c1', 'm1', 0, { content: 'the last one', unreadable: null })

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: 'the last one', unreadable: null })
  })
})

describe('applyOpenedPreview', () => {
  const pending = (overrides: Partial<NonNullable<Conversation['lastMessage']>> = {}): NonNullable<Conversation['lastMessage']> => ({
    senderId: 'bob',
    content: null,
    unreadable: 'pending',
    createdAt: '2026-10-11T10:00:00Z',
    attachmentCount: 0,
    sealed: { ciphertext: 'c', keyEpoch: 1, clientMessageId: 'cid', editCount: 0 },
    ...overrides,
  })

  it('puts the opened preview in place of the one that said it was being opened', () => {
    store().seedConversations([conversation({ id: 'c1', lastMessage: pending() })])

    store().applyOpenedPreview('c1', pending({ content: 'hello', unreadable: null }))

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: 'hello', unreadable: null })
  })

  it('ignores a preview of a message that is no longer the last one, in sender, time or edit', () => {
    store().seedConversations([conversation({ id: 'c1', lastMessage: pending() })])

    store().applyOpenedPreview('c1', pending({ senderId: 'carol', content: 'a', unreadable: null }))
    store().applyOpenedPreview('c1', pending({ createdAt: '2026-10-11T10:05:00Z', content: 'b', unreadable: null }))
    store().applyOpenedPreview(
      'c1',
      pending({ sealed: { ciphertext: 'c', keyEpoch: 1, clientMessageId: 'cid', editCount: 1 }, content: 'c', unreadable: null }),
    )

    expect(store().conversations.c1.lastMessage?.unreadable).toBe('pending')
  })

  it('ignores a preview for a conversation that is not held, or that has no message', () => {
    store().seedConversations([conversation({ id: 'c1' })])
    const before = store().conversations

    store().applyOpenedPreview('c1', pending({ content: 'a', unreadable: null }))
    store().applyOpenedPreview('nope', pending({ content: 'a', unreadable: null }))

    expect(store().conversations).toBe(before)
  })
})

describe('a list that arrives while the previews held are already opened', () => {
  const sealedPreview = { ciphertext: 'c', keyEpoch: 1, clientMessageId: 'cid', editCount: 0 }
  const row = (content: string | null, unreadable: 'pending' | null, editCount = 0) =>
    conversation({
      id: 'c1',
      updatedAt: '2026-10-11T10:00:00Z',
      lastMessage: {
        senderId: 'bob',
        content,
        unreadable,
        createdAt: '2026-10-11T10:00:00Z',
        attachmentCount: 0,
        sealed: { ...sealedPreview, editCount },
      },
    })

  it('keeps the opened preview of the same message, and does not go back to waiting', () => {
    store().seedConversations([row('opened text', null)])

    store().seedConversations([row(null, 'pending')])

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: 'opened text', unreadable: null })
  })

  it('takes the new one when the message was edited since', () => {
    store().seedConversations([row('opened text', null)])

    store().seedConversations([row(null, 'pending', 1)])

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: null, unreadable: 'pending' })
  })

  it('takes an opened preview that arrives over one that is waiting', () => {
    store().seedConversations([row(null, 'pending')])

    store().seedConversations([row('opened text', null)])

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: 'opened text', unreadable: null })
  })
})
