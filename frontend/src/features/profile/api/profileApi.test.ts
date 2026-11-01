import axios, { type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useAuthStore } from '../../auth/store/authStore'
import { UploadFailed } from '../../chat/api/uploadsApi'
import { useChatStore } from '../../chat/store/chatStore'
import type { Conversation } from '../../chat/types'
import { fetchMe, removePicture, setPicture, updateDisplayName } from './profileApi'

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

const ticket = { uploadKey: 'pending/avatars/users/me/k1', url: 'https://bucket.test/put', expiresAt: '2026-11-01T10:00:00Z' }
const picture = new Blob(['x'], { type: 'image/webp' })

function group(overrides: Partial<Conversation> = {}): Conversation {
  return {
    id: 'g1',
    type: 'Group',
    name: 'Team',
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

beforeEach(() => {
  useChatStore.getState().reset()
  useAuthStore.setState({
    status: 'authenticated',
    token: 'jwt',
    tokenExpiresAt: null,
    user: { userId: 'me', userName: 'alice', displayName: 'Alice', avatarUrl: null },
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('loading and changing the profile', () => {
  it('puts the profile the server returns into the signed-in user', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(
      respond({ id: 'me', userName: 'alice', displayName: 'Alice', avatarUrl: '/api/avatars/users/me/f1' }),
    )

    await fetchMe()

    expect(useAuthStore.getState().user?.avatarUrl).toBe('/api/avatars/users/me/f1')
  })

  it('shows a new name only after the server has accepted it, and in the places the chat shows it', async () => {
    useChatStore.getState().seedMessages('c1', [
      {
        id: 'm1', conversationId: 'c1', senderId: 'me', senderDisplayName: 'Alice', content: 'hi',
        createdAt: '2026-10-11T10:00:00Z', clientMessageId: null, editedAt: null, editCount: 0, isDeleted: false, attachments: [],
      },
    ])
    const patch = vi.spyOn(api, 'patch').mockResolvedValue(
      respond({ id: 'me', userName: 'alice', displayName: 'Alice B', avatarUrl: null }),
    )

    await updateDisplayName('Alice B')

    expect(patch).toHaveBeenCalledWith('/users/me', { displayName: 'Alice B' })
    expect(useAuthStore.getState().user?.displayName).toBe('Alice B')
    expect(useChatStore.getState().messages.c1[0].senderDisplayName).toBe('Alice B')
  })

  it('leaves the name as it was when the server refuses', async () => {
    vi.spyOn(api, 'patch').mockRejectedValue(new Error('refused'))

    await expect(updateDisplayName('Alice B')).rejects.toThrow('refused')

    expect(useAuthStore.getState().user?.displayName).toBe('Alice')
  })
})

describe('setting a picture', () => {
  it('asks for permission, sends the picture to the storage without the API token, then names it', async () => {
    const calls: string[] = []
    const post = vi.spyOn(api, 'post').mockImplementation(async () => {
      calls.push('ticket')
      return respond(ticket)
    })
    const put = vi.spyOn(axios, 'put').mockImplementation(async () => {
      calls.push('storage')
      return respond(null)
    })
    const apiPut = vi.spyOn(api, 'put').mockImplementation(async () => {
      calls.push('name')
      return respond({ avatarUrl: '/api/avatars/users/me/f2' })
    })

    const address = await setPicture({ kind: 'me' }, picture)

    expect(calls).toEqual(['ticket', 'storage', 'name'])
    expect(post).toHaveBeenCalledWith('/users/me/avatar/uploads', { contentType: 'image/webp', sizeBytes: picture.size })
    expect(put).toHaveBeenCalledWith(ticket.url, picture, { headers: { 'Content-Type': 'image/webp' } })
    expect(apiPut).toHaveBeenCalledWith('/users/me/avatar', { uploadKey: ticket.uploadKey })
    expect(address).toBe('/api/avatars/users/me/f2')
    expect(useAuthStore.getState().user?.avatarUrl).toBe('/api/avatars/users/me/f2')
  })

  it('does not name the picture when the storage refused it, and says so in words a person can read', async () => {
    vi.spyOn(api, 'post').mockResolvedValue(respond(ticket))
    vi.spyOn(axios, 'put').mockRejectedValue(new Error('403'))
    const apiPut = vi.spyOn(api, 'put')

    await expect(setPicture({ kind: 'me' }, picture)).rejects.toBeInstanceOf(UploadFailed)

    expect(apiPut).not.toHaveBeenCalled()
    expect(useAuthStore.getState().user?.avatarUrl).toBeNull()
  })

  it('uses the group paths for a group and puts the picture on the conversation it names', async () => {
    useChatStore.getState().seedConversations([group()])
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(ticket))
    vi.spyOn(axios, 'put').mockResolvedValue(respond(null))
    const apiPut = vi.spyOn(api, 'put').mockResolvedValue(respond({ avatarUrl: '/api/avatars/groups/g1/f1' }))

    await setPicture({ kind: 'group', conversationId: 'g1' }, picture)

    expect(post).toHaveBeenCalledWith('/conversations/g1/avatar/uploads', expect.anything())
    expect(apiPut).toHaveBeenCalledWith('/conversations/g1/avatar', { uploadKey: ticket.uploadKey })
    expect(useChatStore.getState().conversations.g1).toMatchObject({ name: 'Team', avatarUrl: '/api/avatars/groups/g1/f1' })
    expect(useAuthStore.getState().user?.avatarUrl).toBeNull()
  })
})

describe('removing a picture', () => {
  it('takes it away from the signed-in user once the server has', async () => {
    useAuthStore.setState((state) => ({ user: { ...state.user!, avatarUrl: '/api/avatars/users/me/f1' } }))
    const remove = vi.spyOn(api, 'delete').mockResolvedValue(respond({ avatarUrl: null }))

    await removePicture({ kind: 'me' })

    expect(remove).toHaveBeenCalledWith('/users/me/avatar')
    expect(useAuthStore.getState().user?.avatarUrl).toBeNull()
  })

  it('keeps the picture when the server refuses', async () => {
    useAuthStore.setState((state) => ({ user: { ...state.user!, avatarUrl: '/api/avatars/users/me/f1' } }))
    vi.spyOn(api, 'delete').mockRejectedValue(new Error('refused'))

    await expect(removePicture({ kind: 'me' })).rejects.toThrow('refused')

    expect(useAuthStore.getState().user?.avatarUrl).toBe('/api/avatars/users/me/f1')
  })

  it('takes a group picture away from the conversation', async () => {
    useChatStore.getState().seedConversations([group({ avatarUrl: '/api/avatars/groups/g1/f1' })])
    const remove = vi.spyOn(api, 'delete').mockResolvedValue(respond({ avatarUrl: null }))

    await removePicture({ kind: 'group', conversationId: 'g1' })

    expect(remove).toHaveBeenCalledWith('/conversations/g1/avatar')
    expect(useChatStore.getState().conversations.g1.avatarUrl).toBeNull()
  })
})
