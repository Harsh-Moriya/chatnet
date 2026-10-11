import { beforeEach, describe, expect, it } from 'vitest'
import { sortedConversations, useChatStore } from './chatStore'
import type { Conversation, Message, PendingMessage } from '../types'

const store = () => useChatStore.getState()

function message(overrides: Partial<Message> & { id: string }): Message {
  return {
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: `content of ${overrides.id}`,
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
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
    ...overrides,
  }
}

function pending(clientMessageId: string, overrides: Partial<PendingMessage> = {}): PendingMessage {
  return {
    clientMessageId,
    conversationId: 'c1',
    content: `pending ${clientMessageId}`,
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

  it('keeps the client id of a message when a history load returns it without one', () => {
    store().receiveMessage(message({ id: 'm', clientMessageId: 'p1' }))

    store().seedMessages('c1', [message({ id: 'm', clientMessageId: null })])

    expect(store().messages.c1[0].clientMessageId).toBe('p1')
  })

  it('lets the server copy win when a message changed (for example it was deleted)', () => {
    store().receiveMessage(message({ id: 'm', content: 'secret' }))

    store().seedMessages('c1', [message({ id: 'm', content: null })])

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
    expect(list[0].lastMessage).toEqual({ senderId: 'bob', content: 'hello', createdAt: '2026-10-11T10:00:00Z' })
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
        lastMessage: { senderId: 'x', content: 'from another device', createdAt: '2026-10-11T11:00:00Z' },
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
  })
})
