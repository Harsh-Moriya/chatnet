import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Message } from '../types'
import { deleteMessage, editMessage } from './chatApi'
import { keysDouble, resetKeysDouble, sealed, wire } from './keysDouble'

// The keys of a conversation need a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('./keysDouble')).keysModule)

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
    clientMessageId: 'cid-1',
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
    keyEpoch: 0,
    keyRotationRequired: false,
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
  resetKeysDouble()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('editMessage', () => {
  it('patches the message with the new text and puts the answer in the store', async () => {
    store().seedMessages('c1', [message()])
    const patch = vi
      .spyOn(api, 'patch')
      .mockResolvedValue(respond(wire(message({ content: 'second draft', editCount: 1, editedAt: '2026-10-11T10:02:00Z' }))))

    const result = await editMessage('c1', 'm1', 'second draft', 'me')

    expect(patch).toHaveBeenCalledWith('/conversations/c1/messages/m1', {
      ciphertext: sealed('second draft'),
      editCount: 1,
      mentionedUserIds: [],
    })
    expect(result.content).toBe('second draft')
    expect(store().messages.c1[0]).toMatchObject({ content: 'second draft', editCount: 1 })
  })

  it('seals the new text for the next edit count under the id of the message, trimmed', async () => {
    store().seedMessages('c1', [message({ editCount: 2 })])
    vi.spyOn(api, 'patch').mockResolvedValue(respond(wire(message({ content: 'third draft', editCount: 3 }))))

    await editMessage('c1', 'm1', '  third draft \n', 'me')

    expect(keysDouble.sealAndSend).toHaveBeenCalledWith(
      'c1',
      { keyEpoch: 0, keyRotationRequired: false },
      { clientMessageId: 'cid-1', editCount: 3, text: 'third draft', files: [] },
      expect.any(Function),
    )
  })

  it('seals the entries of the files into the edit again, in order, or nobody could open the files', async () => {
    const first = { name: 'a.png', type: 'image/png', size: 10, key: 'a'.repeat(43), chunkSize: 65536 }
    const second = { name: 'b.pdf', type: 'application/pdf', size: 20, key: 'b'.repeat(43), chunkSize: 65536 }
    const held = [
      { id: 'a1', sizeBytes: 39, url: 'https://files.test/a1', file: first },
      { id: 'a2', sizeBytes: 49, url: 'https://files.test/a2', file: second },
    ]
    store().seedMessages('c1', [message({ attachments: held })])
    const patch = vi.spyOn(api, 'patch').mockResolvedValue(respond(wire(message({ content: 'new', editCount: 1, attachments: held }))))

    const result = await editMessage('c1', 'm1', 'new', 'me')

    expect(patch.mock.calls[0][1]).toMatchObject({ ciphertext: sealed('new', [first, second]) })
    expect(result.attachments.map((a) => a.file)).toEqual([first, second])
  })

  it('refuses at once to edit a message with a file that the sealed message does not vouch for', async () => {
    const entry = { name: 'a.png', type: 'image/png', size: 10, key: 'a'.repeat(43), chunkSize: 65536 }
    store().seedMessages('c1', [
      message({
        attachments: [
          { id: 'a1', sizeBytes: 39, url: 'https://files.test/a1', file: entry },
          { id: 'a2', sizeBytes: 49, url: 'https://files.test/a2', file: null },
        ],
      }),
    ])
    const patch = vi.spyOn(api, 'patch')

    await expect(editMessage('c1', 'm1', 'x', 'me')).rejects.toThrow('cannot be opened')

    expect(patch).not.toHaveBeenCalled()
    expect(keysDouble.sealAndSend).not.toHaveBeenCalled()
  })

  it('goes by the keys the conversation is known to be in', async () => {
    store().seedConversations([conversation({ keyEpoch: 4, keyRotationRequired: true })])
    store().seedMessages('c1', [message()])
    vi.spyOn(api, 'patch').mockResolvedValue(respond(wire(message({ content: 'x', editCount: 1 }))))

    await editMessage('c1', 'm1', 'x', 'me')

    expect(keysDouble.sealAndSend.mock.calls[0][1]).toEqual({ keyEpoch: 4, keyRotationRequired: true })
  })

  it('says who the new text names, as ids, for the members of the conversation', async () => {
    store().seedMembers('c1', [
      { userId: 'me', userName: 'me_user', displayName: 'Me', avatarUrl: null, role: 'Owner', lastReadAt: null },
      { userId: 'bob', userName: 'bob_99', displayName: 'Bob', avatarUrl: null, role: 'Member', lastReadAt: null },
    ])
    store().seedMessages('c1', [message()])
    const patch = vi.spyOn(api, 'patch').mockResolvedValue(respond(wire(message({ content: 'hi @bob_99', editCount: 1 }))))

    await editMessage('c1', 'm1', 'hi @bob_99 and @me_user', 'me')

    expect(patch.mock.calls[0][1]).toMatchObject({ mentionedUserIds: ['bob'] })
  })

  it('refuses at once a message that the server has not confirmed, which has no id to seal under', async () => {
    store().seedMessages('c1', [message({ clientMessageId: null })])
    const patch = vi.spyOn(api, 'patch')

    await expect(editMessage('c1', 'm1', 'x', 'me')).rejects.toThrow('not loaded')

    expect(patch).not.toHaveBeenCalled()
    expect(keysDouble.sealAndSend).not.toHaveBeenCalled()
  })

  it('refuses at once, asking nothing of the server, a message that is not loaded', async () => {
    const patch = vi.spyOn(api, 'patch')

    await expect(editMessage('c1', 'unknown', 'x', 'me')).rejects.toThrow('not loaded')

    expect(patch).not.toHaveBeenCalled()
    expect(keysDouble.sealAndSend).not.toHaveBeenCalled()
  })

  it('says in its own words that the message was edited somewhere else first', async () => {
    store().seedMessages('c1', [message()])
    vi.spyOn(api, 'patch').mockRejectedValue(
      new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
        status: 409,
        data: { title: 'Conflict', status: 409, detail: 'This message was edited somewhere else first.', code: 'edit-count-stale' },
      } as AxiosResponse),
    )

    await expect(editMessage('c1', 'm1', 'x', 'me')).rejects.toThrow('edited somewhere else')
  })

  it('changes nothing and rejects with the server\'s reason when it is refused', async () => {
    store().seedMessages('c1', [message()])
    const before = store().messages
    vi.spyOn(api, 'patch').mockRejectedValue(refusal(409, 'This message can no longer be edited.'))

    await expect(editMessage('c1', 'm1', 'too late', 'me')).rejects.toThrow('This message can no longer be edited.')

    expect(store().messages).toBe(before)
  })

  it('applies an answer that arrives after the push of the same edit without changing anything twice', async () => {
    store().seedMessages('c1', [message()])
    const edited = message({ content: 'second draft', editCount: 1 })
    store().replaceMessage(edited, 'me')
    const before = store().messages
    vi.spyOn(api, 'patch').mockResolvedValue(respond(wire(edited)))

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
    const remove = vi.spyOn(api, 'delete').mockResolvedValue(respond(wire(message({ isDeleted: true, content: null }))))

    await deleteMessage('c1', 'm1', 'me')

    expect(remove).toHaveBeenCalledWith('/conversations/c1/messages/m1')
    expect(store().messages.c1[0]).toMatchObject({ isDeleted: true, content: null })
    expect(store().conversations.c1.lastMessage?.content).toBeNull()
  })

  it('takes an admin\'s deletion of someone else\'s unread message off the count', async () => {
    store().seedConversations([conversation({ type: 'Group', unreadCount: 1 })])
    store().seedMessages('c1', [message({ senderId: 'bob' })])
    vi.spyOn(api, 'delete').mockResolvedValue(respond(wire(message({ senderId: 'bob', isDeleted: true, content: null }))))

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
