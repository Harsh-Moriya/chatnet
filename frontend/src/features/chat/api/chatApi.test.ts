import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Member, Message, UserSummary } from '../types'
import {
  createDirect,
  createGroup,
  dismissMessage,
  fetchConversations,
  fetchMembers,
  fetchMessages,
  markRead,
  retryMessage,
  searchUsers,
  sendMessage,
} from './chatApi'

const store = () => useChatStore.getState()

// ##### Helpers #####

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

function failure(status: number, detail: string): AxiosError {
  return new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
    status,
    data: { detail },
  } as AxiosResponse)
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function message(overrides: Partial<Message> & { id: string }): Message {
  return {
    conversationId: 'c1',
    senderId: 'me',
    senderDisplayName: 'Me',
    content: 'hello',
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    ...overrides,
  }
}

function conversation(id: string): Conversation {
  return { id, type: 'Direct', name: 'Bob', avatarUrl: null, updatedAt: '2026-10-11T09:00:00Z', lastMessage: null, unreadCount: 0, otherUserId: null }
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0))

beforeEach(() => {
  store().reset()
})

afterEach(() => {
  vi.restoreAllMocks()
})

// ##### Loading data #####

describe('loading the conversation list', () => {
  it('waits for the live connection before asking the server', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([conversation('c1')]))

    const loading = fetchConversations()
    await flush()
    expect(get).not.toHaveBeenCalled()

    store().setConnection('connected')
    await loading
    expect(get).toHaveBeenCalledTimes(1)
    expect(get.mock.calls[0][0]).toBe('/conversations')
  })

  it('puts the list in the store and returns it', async () => {
    store().setConnection('connected')
    vi.spyOn(api, 'get').mockResolvedValue(respond([conversation('c1'), conversation('c2')]))

    const list = await fetchConversations()

    expect(list.map((c) => c.id)).toEqual(['c1', 'c2'])
    expect(Object.keys(store().conversations).sort()).toEqual(['c1', 'c2'])
  })

  it('does not ask the server at all if cancelled while waiting', async () => {
    const get = vi.spyOn(api, 'get')
    const controller = new AbortController()

    const loading = fetchConversations(controller.signal)
    controller.abort(new Error('cancelled'))

    await expect(loading).rejects.toThrow('cancelled')
    expect(get).not.toHaveBeenCalled()
  })

  it('hands the cancel signal to the request', async () => {
    store().setConnection('connected')
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))
    const controller = new AbortController()

    await fetchConversations(controller.signal)

    expect(get.mock.calls[0][1]).toMatchObject({ signal: controller.signal })
  })
})

describe('loading a conversation\'s history', () => {
  it('waits for the live connection before asking the server', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([message({ id: 'a' })]))

    const loading = fetchMessages('c1')
    await flush()
    expect(get).not.toHaveBeenCalled()

    store().setConnection('connected')
    await loading
    expect(get.mock.calls[0][0]).toBe('/conversations/c1/messages')
  })

  it('merges the history into the store without losing a message pushed meanwhile', async () => {
    store().setConnection('connected')
    const response = deferred<AxiosResponse<Message[]>>()
    vi.spyOn(api, 'get').mockReturnValue(response.promise)

    const loading = fetchMessages('c1')
    store().receiveMessage(message({ id: 'pushed', createdAt: '2026-10-11T10:00:09Z' }))
    response.resolve(respond([message({ id: 'old', createdAt: '2026-10-11T10:00:01Z' })]))
    await loading

    expect(store().messages.c1.map((m) => m.id)).toEqual(['old', 'pushed'])
  })

  it('asks for the latest page when no cursor is given', async () => {
    store().setConnection('connected')
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))

    await fetchMessages('c1')

    expect(get.mock.calls[0][1]).toMatchObject({ params: { before: undefined, after: undefined } })
  })

  it('asks for the page before a message and hands over the cancel signal', async () => {
    store().setConnection('connected')
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))
    const controller = new AbortController()

    await fetchMessages('c1', { before: 'oldest-known', signal: controller.signal })

    expect(get.mock.calls[0][1]).toMatchObject({
      params: { before: 'oldest-known', after: undefined },
      signal: controller.signal,
    })
  })

  it('asks for the page after a message', async () => {
    store().setConnection('connected')
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))

    await fetchMessages('c1', { after: 'newest-known' })

    expect(get.mock.calls[0][1]).toMatchObject({ params: { before: undefined, after: 'newest-known' } })
  })

  it('adds an older page above the messages already shown, in order', async () => {
    store().setConnection('connected')
    store().seedMessages('c1', [
      message({ id: 'c', createdAt: '2026-10-11T10:00:30Z' }),
      message({ id: 'd', createdAt: '2026-10-11T10:00:40Z' }),
    ])
    vi.spyOn(api, 'get').mockResolvedValue(
      respond([
        message({ id: 'a', createdAt: '2026-10-11T10:00:10Z' }),
        message({ id: 'b', createdAt: '2026-10-11T10:00:20Z' }),
      ]),
    )

    await fetchMessages('c1', { before: 'c' })

    expect(store().messages.c1.map((m) => m.id)).toEqual(['a', 'b', 'c', 'd'])
  })

  it('does not duplicate a message that two pages both contain', async () => {
    store().setConnection('connected')
    store().seedMessages('c1', [message({ id: 'b', createdAt: '2026-10-11T10:00:20Z' })])
    vi.spyOn(api, 'get').mockResolvedValue(
      respond([
        message({ id: 'a', createdAt: '2026-10-11T10:00:10Z' }),
        message({ id: 'b', createdAt: '2026-10-11T10:00:20Z' }),
      ]),
    )

    await fetchMessages('c1', { before: 'b' })

    expect(store().messages.c1.map((m) => m.id)).toEqual(['a', 'b'])
  })

  it('leaves the store alone when the request fails', async () => {
    store().setConnection('connected')
    store().receiveMessage(message({ id: 'kept' }))
    vi.spyOn(api, 'get').mockRejectedValue(failure(403, 'You are not a member of this conversation.'))

    await expect(fetchMessages('c1')).rejects.toThrow()

    expect(store().messages.c1.map((m) => m.id)).toEqual(['kept'])
  })
})

describe('searching for people', () => {
  it('asks the server and returns the matches', async () => {
    const people: UserSummary[] = [{ id: 'u1', userName: 'bob', displayName: 'Bob', avatarUrl: null }]
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(people))

    const found = await searchUsers('bo')

    expect(found).toEqual(people)
    expect(get).toHaveBeenCalledWith('/users', expect.objectContaining({ params: { query: 'bo' } }))
  })

  it('does not wait for the live connection, because it loads nothing the connection pushes', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(respond([]))

    await expect(searchUsers('bo')).resolves.toEqual([])
  })
})

// ##### Starting conversations #####

describe('starting a conversation', () => {
  it('adds a direct conversation to the sidebar straight away', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(conversation('new')))

    const created = await createDirect('u1')

    expect(created.id).toBe('new')
    expect(post).toHaveBeenCalledWith('/conversations/direct', { otherUserId: 'u1' })
    expect(store().conversations.new).toBeDefined()
  })

  it('adds a group to the sidebar straight away', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond({ ...conversation('team'), type: 'Group', name: 'Team' }))

    const created = await createGroup('Team', ['u1', 'u2'])

    expect(created.name).toBe('Team')
    expect(post).toHaveBeenCalledWith('/conversations/group', { name: 'Team', memberIds: ['u1', 'u2'] })
    expect(store().conversations.team.type).toBe('Group')
  })

  it('adds nothing when the server refuses', async () => {
    vi.spyOn(api, 'post').mockRejectedValue(failure(404, 'User with ID \'x\' was not found.'))

    await expect(createDirect('x')).rejects.toThrow()

    expect(store().conversations).toEqual({})
  })
})

// ##### Sending #####

describe('sending a message', () => {
  it('shows the message at once as sending, before the server answers', async () => {
    const response = deferred<AxiosResponse<Message>>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)

    const sending = sendMessage('c1', 'Hello')

    expect(store().pending.c1).toHaveLength(1)
    expect(store().pending.c1[0]).toMatchObject({ content: 'Hello', status: 'sending', error: null })

    response.resolve(respond(message({ id: 'real', content: 'Hello', clientMessageId: store().pending.c1[0].clientMessageId })))
    await sending
  })

  it('sends the trimmed text with the id it invented for the unconfirmed copy', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(message({ id: 'real' })))

    await sendMessage('c1', '  Hello  \n')

    const [url, body, config] = post.mock.calls[0]
    expect(url).toBe('/conversations/c1/messages')
    expect(body).toEqual({ content: 'Hello', clientMessageId: expect.stringMatching(/^[0-9a-f-]{36}$/) })
    expect(config).toMatchObject({ timeout: expect.any(Number) })
  })

  it('replaces the unconfirmed copy with the server\'s message', async () => {
    vi.spyOn(api, 'post').mockImplementation((_url, body) =>
      Promise.resolve(respond(message({ id: 'real', content: 'Hello', clientMessageId: (body as { clientMessageId: string }).clientMessageId }))),
    )

    await sendMessage('c1', 'Hello')

    expect(store().pending.c1).toEqual([])
    expect(store().messages.c1.map((m) => m.id)).toEqual(['real'])
  })

  it('shows the message once when the push arrives before the response', async () => {
    const response = deferred<AxiosResponse<Message>>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)
    const sending = sendMessage('c1', 'Hello')
    const confirmed = message({ id: 'real', content: 'Hello', clientMessageId: store().pending.c1[0].clientMessageId })

    store().receiveMessage(confirmed)
    response.resolve(respond(confirmed))
    await sending

    expect(store().messages.c1.map((m) => m.id)).toEqual(['real'])
    expect(store().pending.c1).toEqual([])
  })

  it('marks the message failed with the server\'s reason when it is refused', async () => {
    vi.spyOn(api, 'post').mockRejectedValue(failure(403, 'You are not a member of this conversation.'))

    await sendMessage('c1', 'Hello')

    expect(store().pending.c1[0]).toMatchObject({
      status: 'failed',
      error: 'You are not a member of this conversation.',
    })
    expect(store().messages.c1).toBeUndefined()
  })

  it('does not mark a message failed that the push already confirmed', async () => {
    const response = deferred<AxiosResponse<Message>>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)
    const sending = sendMessage('c1', 'Hello')
    store().receiveMessage(message({ id: 'real', clientMessageId: store().pending.c1[0].clientMessageId }))

    response.reject(new AxiosError('timeout of 15000ms exceeded', 'ECONNABORTED'))
    await sending

    expect(store().pending.c1).toEqual([])
    expect(store().messages.c1.map((m) => m.id)).toEqual(['real'])
  })

  it('does nothing for a message that is empty after trimming', async () => {
    const post = vi.spyOn(api, 'post')

    await sendMessage('c1', '   \n ')

    expect(post).not.toHaveBeenCalled()
    expect(store().pending.c1).toBeUndefined()
  })

  it('keeps several messages in the order they were sent', async () => {
    vi.spyOn(api, 'post').mockReturnValue(new Promise(() => {}))

    void sendMessage('c1', 'one')
    void sendMessage('c1', 'two')
    void sendMessage('c1', 'three')

    expect(store().pending.c1.map((p) => p.content)).toEqual(['one', 'two', 'three'])
    expect(new Set(store().pending.c1.map((p) => p.clientMessageId)).size).toBe(3)
  })
})

describe('retrying and dismissing', () => {
  async function failedMessage() {
    vi.spyOn(api, 'post').mockRejectedValueOnce(new AxiosError('Network Error', 'ERR_NETWORK'))
    await sendMessage('c1', 'Hello')
    return store().pending.c1[0]
  }

  it('sends again with the same id, so the push of the first attempt would still match it', async () => {
    const failed = await failedMessage()
    const post = vi.spyOn(api, 'post').mockResolvedValue(
      respond(message({ id: 'real', content: 'Hello', clientMessageId: failed.clientMessageId })),
    )

    await retryMessage('c1', failed.clientMessageId)

    expect(post.mock.calls[0][1]).toEqual({ content: 'Hello', clientMessageId: failed.clientMessageId })
    expect(store().pending.c1).toEqual([])
    expect(store().messages.c1.map((m) => m.id)).toEqual(['real'])
  })

  // The first attempt reached the server and only its answer was lost. The retry is told so: the stored
  // message comes back with 200 instead of 201. A client does not look at which, and the message must
  // replace the unconfirmed copy and appear once.
  it('treats the stored message returned for a repeat as the confirmation, like a new one', async () => {
    const failed = await failedMessage()
    const stored = message({ id: 'stored', content: 'Hello', clientMessageId: failed.clientMessageId })
    vi.spyOn(api, 'post').mockResolvedValue({ data: stored, status: 200 } as AxiosResponse<Message>)

    await retryMessage('c1', failed.clientMessageId)

    expect(store().pending.c1).toEqual([])
    expect(store().messages.c1.map((m) => m.id)).toEqual(['stored'])
  })

  it('shows the message once when the retry is answered after the push and after a history load', async () => {
    const failed = await failedMessage()
    const stored = message({ id: 'stored', content: 'Hello', clientMessageId: failed.clientMessageId })
    store().receiveMessage(stored, 'me')
    store().seedMessages('c1', [stored])
    vi.spyOn(api, 'post').mockResolvedValue({ data: stored, status: 200 } as AxiosResponse<Message>)

    await retryMessage('c1', failed.clientMessageId)

    expect(store().messages.c1.map((m) => m.id)).toEqual(['stored'])
    expect(store().pending.c1).toEqual([])
  })

  it('shows it as sending again while the retry is in flight', async () => {
    const failed = await failedMessage()
    vi.spyOn(api, 'post').mockReturnValue(new Promise(() => {}))

    void retryMessage('c1', failed.clientMessageId)

    expect(store().pending.c1[0]).toMatchObject({ status: 'sending', error: null })
  })

  it('can fail again', async () => {
    const failed = await failedMessage()
    vi.spyOn(api, 'post').mockRejectedValue(failure(400, 'Bad.'))

    await retryMessage('c1', failed.clientMessageId)

    expect(store().pending.c1[0]).toMatchObject({ status: 'failed', error: 'Bad.' })
  })

  it('ignores a message that is not failed, so a double click cannot send twice', async () => {
    vi.spyOn(api, 'post').mockReturnValue(new Promise(() => {}))
    void sendMessage('c1', 'Hello')
    const post = vi.spyOn(api, 'post')
    post.mockClear()

    await retryMessage('c1', store().pending.c1[0].clientMessageId)

    expect(post).not.toHaveBeenCalled()
  })

  it('ignores an unknown message', async () => {
    const post = vi.spyOn(api, 'post')

    await retryMessage('c1', 'nope')

    expect(post).not.toHaveBeenCalled()
  })

  it('can be dismissed', async () => {
    const failed = await failedMessage()

    dismissMessage('c1', failed.clientMessageId)

    expect(store().pending.c1).toEqual([])
  })
})

// ##### Members and reading #####

function member(userId: string, lastReadAt: string | null = null): Member {
  return { userId, displayName: userId, avatarUrl: null, role: 'Member', lastReadAt }
}

describe('loading the members of a conversation', () => {
  it('waits for the live connection before asking the server', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([member('me')]))

    const loading = fetchMembers('c1')
    await flush()
    expect(get).not.toHaveBeenCalled()

    store().setConnection('connected')
    await loading
    expect(get.mock.calls[0][0]).toBe('/conversations/c1/members')
  })

  it('puts the members in the store and returns them', async () => {
    store().setConnection('connected')
    vi.spyOn(api, 'get').mockResolvedValue(respond([member('me'), member('bob', '2026-10-11T10:00:00Z')]))

    const members = await fetchMembers('c1')

    expect(members.map((m) => m.userId)).toEqual(['me', 'bob'])
    expect(store().members.c1.map((m) => m.userId)).toEqual(['me', 'bob'])
  })

  it('leaves the store alone when the request fails', async () => {
    store().setConnection('connected')
    store().seedMembers('c1', [member('kept')])
    vi.spyOn(api, 'get').mockRejectedValue(failure(403, 'You are not a member of this conversation.'))

    await expect(fetchMembers('c1')).rejects.toThrow()

    expect(store().members.c1.map((m) => m.userId)).toEqual(['kept'])
  })
})

describe('telling the server a conversation was read', () => {
  function withUnread() {
    store().seedConversations([
      {
        ...conversation('c1'),
        unreadCount: 2,
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T10:00:02Z' },
      },
    ])
    store().seedMessages('c1', [
      message({ id: 'm1', senderId: 'bob', createdAt: '2026-10-11T10:00:01Z' }),
      message({ id: 'm2', senderId: 'bob', createdAt: '2026-10-11T10:00:02Z' }),
    ])
  }

  it('posts the id of the message read up to', async () => {
    withUnread()
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(undefined))

    await markRead('c1', 'm2', 'me')

    expect(post).toHaveBeenCalledWith('/conversations/c1/read', { messageId: 'm2' })
  })

  it('clears the count once the server has accepted it, without waiting for the push', async () => {
    withUnread()
    vi.spyOn(api, 'post').mockResolvedValue(respond(undefined))

    await markRead('c1', 'm2', 'me')

    expect(store().conversations.c1.unreadCount).toBe(0)
  })

  it('keeps the count while the server has not accepted it', async () => {
    withUnread()
    const response = deferred<AxiosResponse<undefined>>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)

    const reading = markRead('c1', 'm2', 'me')

    expect(store().conversations.c1.unreadCount).toBe(2)
    response.resolve(respond(undefined))
    await reading
  })

  it('keeps the count and reports the failure when the server refuses', async () => {
    withUnread()
    vi.spyOn(api, 'post').mockRejectedValue(failure(403, 'You are not a member of this conversation.'))

    await expect(markRead('c1', 'm2', 'me')).rejects.toThrow()

    expect(store().conversations.c1.unreadCount).toBe(2)
  })

  it('only partly lowers the count when the message was not the newest', async () => {
    withUnread()
    vi.spyOn(api, 'post').mockResolvedValue(respond(undefined))

    await markRead('c1', 'm1', 'me')

    expect(store().conversations.c1.unreadCount).toBe(1)
  })
})

describe('sending a message counts as reading', () => {
  it('clears the unread count of the conversation', async () => {
    store().seedConversations([
      {
        ...conversation('c1'),
        unreadCount: 3,
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T09:59:00Z' },
      },
    ])
    vi.spyOn(api, 'post').mockResolvedValue(
      respond(message({ id: 'real', senderId: 'me', createdAt: '2026-10-11T10:00:00Z' })),
    )

    await sendMessage('c1', 'on my way')

    expect(store().conversations.c1.unreadCount).toBe(0)
  })
})
