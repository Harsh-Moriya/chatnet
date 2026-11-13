import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useChatStore } from '../store/chatStore'
import type { Conversation } from '../types'
import { opener, resetKeysDouble, sealed } from './keysDouble'
import { openPreviews } from './previews'

// The keys of a conversation need a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('./keysDouble')).keysModule)

const store = () => useChatStore.getState()

function conversation(id: string, text: string | null, overrides: Partial<Conversation> = {}): Conversation {
  return {
    id,
    type: 'Direct',
    name: `Conversation ${id}`,
    avatarUrl: null,
    updatedAt: '2026-10-11T10:00:00Z',
    lastMessage:
      text === null
        ? null
        : {
            senderId: 'bob',
            content: null,
            unreadable: 'pending',
            createdAt: '2026-10-11T10:00:00Z',
            attachmentCount: 0,
            sealed: { ciphertext: sealed(text), keyEpoch: 1, clientMessageId: `cid-${id}`, editCount: 0 },
          },
    unreadCount: 0,
    otherUserId: 'bob',
    isMuted: false,
    leftAt: null,
    keyEpoch: 1,
    keyRotationRequired: false,
    changeSeq: 0,
    ...overrides,
  }
}

beforeEach(() => {
  store().reset()
  resetKeysDouble()
})

describe('opening the previews of the list', () => {
  it('puts the start of each last message into its row', async () => {
    const list = [conversation('c1', 'first text'), conversation('c2', 'second text')]
    store().seedConversations(list)

    await openPreviews(list)

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: 'first text', unreadable: null })
    expect(store().conversations.c2.lastMessage).toMatchObject({ content: 'second text', unreadable: null })
  })

  it('leaves a conversation with no message, and one whose preview is not waiting to be opened, alone', async () => {
    const empty = conversation('c1', null)
    const done = conversation('c2', 'already shown')
    done.lastMessage = { ...done.lastMessage!, content: 'already shown', unreadable: null }
    store().seedConversations([empty, done])

    await openPreviews([empty, done])

    expect(opener.open).not.toHaveBeenCalled()
    expect(store().conversations.c2.lastMessage?.content).toBe('already shown')
  })

  it('says why a preview cannot be shown, and goes on with the others', async () => {
    const list = [conversation('c1', 'a'), conversation('c2', 'b')]
    store().seedConversations(list)
    opener.open.mockResolvedValueOnce({ readable: false, reason: 'noKey' } as never)

    await openPreviews(list)

    expect(store().conversations.c1.lastMessage).toMatchObject({ content: null, unreadable: 'noKey' })
    expect(store().conversations.c2.lastMessage).toMatchObject({ content: 'b', unreadable: null })
  })

  it('opens no more than three at once, however long the list is', async () => {
    const list = Array.from({ length: 8 }, (_, i) => conversation(`c${i}`, `text ${i}`))
    store().seedConversations(list)
    let active = 0
    let most = 0
    opener.open.mockImplementation(async (message: { ciphertext: string }) => {
      active++
      most = Math.max(most, active)
      await new Promise((resolve) => setTimeout(resolve, 5))
      active--
      return { readable: true as const, text: message.ciphertext.slice('sealed:'.length), files: [] }
    })

    await openPreviews(list)

    expect(most).toBe(3)
    expect(list.every((c) => store().conversations[c.id].lastMessage?.unreadable === null)).toBe(true)
  })

  it('does not put a preview over a newer message that arrived while it was being opened', async () => {
    const list = [conversation('c1', 'old text')]
    store().seedConversations(list)
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
      return { readable: true as const, text: 'old text', files: [] }
    })

    const opening = openPreviews(list)
    await begun
    store().receiveMessage({
      id: 'm2',
      conversationId: 'c1',
      senderId: 'bob',
      senderDisplayName: 'Bob',
      content: 'newer text',
      createdAt: '2026-10-11T10:05:00Z',
      clientMessageId: 'cid-new',
      editedAt: null,
      editCount: 0,
      isDeleted: false,
      attachments: [],
    })
    release()
    await opening

    expect(store().conversations.c1.lastMessage?.content).toBe('newer text')
  })
})
