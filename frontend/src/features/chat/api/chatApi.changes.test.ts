import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Message } from '../types'
import { deleteMessage, editMessage } from './chatApi'

const store = () => useChatStore.getState()

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

function message(overrides: Partial<Message> = {}): Message {
  return {
    id: 'm1',
    conversationId: 'c1',
    senderId: 'me',
    senderDisplayName: 'Me',
    content: 'first draft',
    createdAt: '2026-10-11T10:00:00Z',
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
    updatedAt: '2026-10-11T09:00:00Z',
    lastMessage: null,
    unreadCount: 0,
    otherUserId: null,
    isMuted: false,
    leftAt: null,
    ...overrides,
  }
}

const refusal = (status: number, detail: string) =>
  new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
    status,
    data: { title: 'Conflict', status, detail },
  } as AxiosResponse)

beforeEach(() => {
  store().reset()
  store().setConnection('connected')
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('editMessage', () => {
  it('patches the message with the new text and puts the answer in the store', async () => {
    store().seedMessages('c1', [message()])
    const patch = vi
      .spyOn(api, 'patch')
      .mockResolvedValue(respond(message({ content: 'second draft', editCount: 1, editedAt: '2026-10-11T10:02:00Z' })))

    const result = await editMessage('c1', 'm1', 'second draft', 'me')

    expect(patch).toHaveBeenCalledWith('/conversations/c1/messages/m1', { content: 'second draft' })
    expect(result.content).toBe('second draft')
    expect(store().messages.c1[0]).toMatchObject({ content: 'second draft', editCount: 1 })
  })

  it('changes nothing and rejects with the server\'s reason when it is refused', async () => {
    store().seedMessages('c1', [message()])
    const before = store().messages
    vi.spyOn(api, 'patch').mockRejectedValue(refusal(409, 'This message can no longer be edited.'))

    await expect(editMessage('c1', 'm1', 'too late', 'me')).rejects.toBeInstanceOf(AxiosError)

    expect(store().messages).toBe(before)
  })

  it('applies an answer that arrives after the push of the same edit without changing anything twice', async () => {
    store().seedMessages('c1', [message()])
    const edited = message({ content: 'second draft', editCount: 1 })
    store().replaceMessage(edited, 'me')
    const before = store().messages
    vi.spyOn(api, 'patch').mockResolvedValue(respond(edited))

    await editMessage('c1', 'm1', 'second draft', 'me')

    expect(store().messages).toBe(before)
  })
})

describe('deleteMessage', () => {
  it('deletes the message and puts the deleted copy in the store, with the preview', async () => {
    store().seedConversations([
      conversation({
        lastMessage: { senderId: 'me', content: 'first draft', createdAt: '2026-10-11T10:00:00Z', attachmentCount: 0 },
      }),
    ])
    store().seedMessages('c1', [message()])
    const remove = vi.spyOn(api, 'delete').mockResolvedValue(respond(message({ isDeleted: true, content: null })))

    await deleteMessage('c1', 'm1', 'me')

    expect(remove).toHaveBeenCalledWith('/conversations/c1/messages/m1')
    expect(store().messages.c1[0]).toMatchObject({ isDeleted: true, content: null })
    expect(store().conversations.c1.lastMessage?.content).toBeNull()
  })

  it('takes an admin\'s deletion of someone else\'s unread message off the count', async () => {
    store().seedConversations([conversation({ type: 'Group', unreadCount: 1 })])
    store().seedMessages('c1', [message({ senderId: 'bob' })])
    vi.spyOn(api, 'delete').mockResolvedValue(respond(message({ senderId: 'bob', isDeleted: true, content: null })))

    await deleteMessage('c1', 'm1', 'me')

    expect(store().conversations.c1.unreadCount).toBe(0)
  })

  it('changes nothing and rejects when it is refused', async () => {
    store().seedMessages('c1', [message()])
    const before = store().messages
    vi.spyOn(api, 'delete').mockRejectedValue(refusal(403, 'Only the sender or an admin of the group can delete this message.'))

    await expect(deleteMessage('c1', 'm1', 'me')).rejects.toBeInstanceOf(AxiosError)

    expect(store().messages).toBe(before)
  })
})
