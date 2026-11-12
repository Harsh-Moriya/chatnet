import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { encryptedFileLength } from '../../../lib/crypto/file'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Member, Message, PendingAttachment, UserSummary } from '../types'
import { deps, keysDouble, prepared, resetKeysDouble, sealed, wire } from './keysDouble'
import {
  createDirect,
  createGroup,
  dismissMessage,
  fetchConversations,
  fetchMembers,
  fetchMessages,
  markRead,
  refreshAttachmentUrl,
  requestMessages,
  retryMessage,
  searchUsers,
  sendMessage,
  setMuted,
} from './chatApi'


// The keys of a conversation need a signed-in person and this browser's stores; see keysDouble.ts for what stands in for them.
vi.mock('../../encryption/conversationKeys', async () => (await import('./keysDouble')).keysModule)

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
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    ...overrides,
  }
}

function conversation(id: string): Conversation {
  return { id, type: 'Direct', name: 'Bob', avatarUrl: null, updatedAt: '2026-10-11T09:00:00Z', lastMessage: null, unreadCount: 0, otherUserId: null, isMuted: false, leftAt: null, keyEpoch: 0, keyRotationRequired: false, changeSeq: 0 }
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
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([wire(message({ id: 'a' }))]))

    const loading = fetchMessages('c1')
    await flush()
    expect(get).not.toHaveBeenCalled()

    store().setConnection('connected')
    await loading
    expect(get.mock.calls[0][0]).toBe('/conversations/c1/messages')
  })

  it('merges the history into the store without losing a message pushed meanwhile', async () => {
    store().setConnection('connected')
    const response = deferred<AxiosResponse<unknown>>()
    vi.spyOn(api, 'get').mockReturnValue(response.promise)

    const loading = fetchMessages('c1')
    store().receiveMessage(message({ id: 'pushed', createdAt: '2026-10-11T10:00:09Z' }))
    response.resolve(respond([wire(message({ id: 'old', createdAt: '2026-10-11T10:00:01Z' }))]))
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
        wire(message({ id: 'a', createdAt: '2026-10-11T10:00:10Z' })),
        wire(message({ id: 'b', createdAt: '2026-10-11T10:00:20Z' })),
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
        wire(message({ id: 'a', createdAt: '2026-10-11T10:00:10Z' })),
        wire(message({ id: 'b', createdAt: '2026-10-11T10:00:20Z' })),
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

// The same request as loading a page, for a caller that decides whether the page may be added to the store (the catch-up
// after a reconnect, which must not leave a hole in the history).
describe('requesting a page of history without adding it to the store', () => {
  it('returns the page and leaves the store as it was', async () => {
    store().setConnection('connected')
    store().seedMessages('c1', [message({ id: 'held' })])
    vi.spyOn(api, 'get').mockResolvedValue(respond([wire(message({ id: 'requested', createdAt: '2026-10-11T10:00:09Z' }))]))

    const page = await requestMessages('c1')

    expect(page.map((m) => m.id)).toEqual(['requested'])
    expect(store().messages.c1.map((m) => m.id)).toEqual(['held'])
  })

  it('waits for the live connection before asking the server', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))

    const requesting = requestMessages('c1')
    await flush()
    expect(get).not.toHaveBeenCalled()

    store().setConnection('connected')
    await requesting
    expect(get.mock.calls[0][0]).toBe('/conversations/c1/messages')
  })

  it('asks for the page before or after a message, and hands over the cancel signal', async () => {
    store().setConnection('connected')
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))
    const controller = new AbortController()

    await requestMessages('c1', { before: 'b', signal: controller.signal })
    await requestMessages('c1', { after: 'a' })

    expect(get.mock.calls[0][1]).toMatchObject({ params: { before: 'b', after: undefined }, signal: controller.signal })
    expect(get.mock.calls[1][1]).toMatchObject({ params: { before: undefined, after: 'a' } })
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
  beforeEach(resetKeysDouble)

  it('adds a direct conversation to the sidebar straight away, made with the id and the first epoch prepared for it', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(conversation(prepared.conversationId)))

    const created = await createDirect('u1')

    expect(created.id).toBe(prepared.conversationId)
    expect(keysDouble.prepareNewConversation).toHaveBeenCalledWith(deps, ['u1'])
    expect(post).toHaveBeenCalledWith('/conversations/direct', { id: prepared.conversationId, otherUserId: 'u1', epoch: prepared.body })
    expect(store().conversations[prepared.conversationId]).toBeDefined()
  })

  it('keeps the key it made for the conversation the server answered with', async () => {
    vi.spyOn(api, 'post').mockResolvedValue(respond(conversation(prepared.conversationId)))

    await createDirect('u1')

    expect(keysDouble.keepNewConversationKey).toHaveBeenCalledWith(deps, prepared, prepared.conversationId)
  })

  it('hands the answer to the key it made even when the server found a conversation that already existed', async () => {
    vi.spyOn(api, 'post').mockResolvedValue(respond(conversation('existing')))

    const created = await createDirect('u1')

    expect(created.id).toBe('existing')
    expect(keysDouble.keepNewConversationKey).toHaveBeenCalledWith(deps, prepared, 'existing')
    expect(store().conversations.existing).toBeDefined()
  })

  it('adds a group to the sidebar straight away', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond({ ...conversation('team'), type: 'Group', name: 'Team' }))

    const created = await createGroup('Team', ['u1', 'u2'])

    expect(created.name).toBe('Team')
    expect(keysDouble.prepareNewConversation).toHaveBeenCalledWith(deps, ['u1', 'u2'])
    expect(post).toHaveBeenCalledWith('/conversations/group', {
      id: prepared.conversationId,
      name: 'Team',
      memberIds: ['u1', 'u2'],
      epoch: prepared.body,
    })
    expect(store().conversations.team.type).toBe('Group')
  })

  it('makes the epoch again when the server says the conversation changed, and sends the new one', async () => {
    const stale = new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
      status: 409,
      data: { status: 409, detail: 'Another epoch was stored first.', code: 'epoch-stale' },
    } as AxiosResponse)
    const post = vi
      .spyOn(api, 'post')
      .mockRejectedValueOnce(stale)
      .mockResolvedValueOnce(respond({ ...conversation('team'), type: 'Group', name: 'Team' }))

    await createGroup('Team', ['u1'])

    expect(post).toHaveBeenCalledTimes(2)
    expect(keysDouble.prepareNewConversation).toHaveBeenCalledTimes(2)
  })

  it('does not make it again for a conflict that is not about keys, and passes the server\'s reason on', async () => {
    const taken = new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
      status: 409,
      data: { status: 409, detail: 'Everyone you named is already in the group.' },
    } as AxiosResponse)
    const post = vi.spyOn(api, 'post').mockRejectedValue(taken)

    await expect(createGroup('Team', ['u1'])).rejects.toBe(taken)

    expect(post).toHaveBeenCalledTimes(1)
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
    const response = deferred<AxiosResponse<unknown>>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)

    const sending = sendMessage('c1', 'Hello')

    expect(store().pending.c1).toHaveLength(1)
    expect(store().pending.c1[0]).toMatchObject({ content: 'Hello', status: 'sending', error: null })

    response.resolve(respond(wire(message({ id: 'real', content: 'Hello', clientMessageId: store().pending.c1[0].clientMessageId }))))
    await sending
  })

  it('sends the trimmed text with the id it invented for the unconfirmed copy', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(wire(message({ id: 'real' }))))

    await sendMessage('c1', '  Hello  \n')

    const [url, body, config] = post.mock.calls[0]
    expect(url).toBe('/conversations/c1/messages')
    expect(body).toEqual({
      ciphertext: sealed('Hello'),
      clientMessageId: expect.stringMatching(/^[0-9a-f-]{36}$/),
      mentionedUserIds: [],
    })
    expect(config).toMatchObject({ timeout: expect.any(Number) })
  })

  it('seals the text as the first version of a message, under the id the unconfirmed copy has', async () => {
    vi.spyOn(api, 'post').mockResolvedValue(respond(wire(message({ id: 'real' }))))

    await sendMessage('c1', 'Hello')

    expect(keysDouble.sealAndSend).toHaveBeenCalledWith(
      'c1',
      { keyEpoch: 0, keyRotationRequired: false },
      { clientMessageId: expect.stringMatching(/^[0-9a-f-]{36}$/), editCount: 0, text: 'Hello', files: [] },
      expect.any(Function),
    )
  })

  it('says who the text names, as ids, for the members of the conversation', async () => {
    store().seedMembers('c1', [
      { userId: 'bob', userName: 'bob_99', displayName: 'Bob', avatarUrl: null, role: 'Member', lastReadAt: null },
      { userId: 'cy', userName: 'cy_77', displayName: 'Cy', avatarUrl: null, role: 'Member', lastReadAt: null },
    ])
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(wire(message({ id: 'real' }))))

    await sendMessage('c1', 'hi @bob_99, and @nobody_here')

    expect(post.mock.calls[0][1]).toMatchObject({ mentionedUserIds: ['bob'] })
  })

  it('replaces the unconfirmed copy with the server\'s message', async () => {
    vi.spyOn(api, 'post').mockImplementation((_url, body) =>
      Promise.resolve(respond(wire(message({ id: 'real', content: 'Hello', clientMessageId: (body as { clientMessageId: string }).clientMessageId })))),
    )

    await sendMessage('c1', 'Hello')

    expect(store().pending.c1).toEqual([])
    expect(store().messages.c1.map((m) => m.id)).toEqual(['real'])
  })

  it('shows the message once when the push arrives before the response', async () => {
    const response = deferred<AxiosResponse<unknown>>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)
    const sending = sendMessage('c1', 'Hello')
    const confirmed = message({ id: 'real', content: 'Hello', clientMessageId: store().pending.c1[0].clientMessageId })

    store().receiveMessage(confirmed)
    response.resolve(respond(wire(confirmed)))
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
    const response = deferred<AxiosResponse<unknown>>()
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
      respond(wire(message({ id: 'real', content: 'Hello', clientMessageId: failed.clientMessageId }))),
    )

    await retryMessage('c1', failed.clientMessageId)

    expect(post.mock.calls[0][1]).toEqual({ ciphertext: sealed('Hello'), clientMessageId: failed.clientMessageId, mentionedUserIds: [] })
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
  return { userId, userName: userId, displayName: userId, avatarUrl: null, role: 'Member', lastReadAt }
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
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T10:00:02Z', attachmentCount: 0 },
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
        lastMessage: { senderId: 'bob', content: 'x', createdAt: '2026-10-11T09:59:00Z', attachmentCount: 0 },
      },
    ])
    vi.spyOn(api, 'post').mockResolvedValue(
      respond(wire(message({ id: 'real', senderId: 'me', createdAt: '2026-10-11T10:00:00Z' }))),
    )

    await sendMessage('c1', 'on my way')

    expect(store().conversations.c1.unreadCount).toBe(0)
  })
})

// ##### Sending files #####

function upload(n: number): PendingAttachment {
  return {
    uploadKey: `pending/c1/me/key-${n}`,
    ref: { name: `file ${n}.png`, type: 'image/png', size: 100 * n, key: String(n).repeat(43).slice(0, 43), chunkSize: 65536 },
    file: {} as File,
  }
}

// The file as the server holds it once it is attached: the encrypted size of the upload, and a link.
const stored = (id: string, n: number) => ({ id, sizeBytes: encryptedFileLength(100 * n, 65536), url: `https://files.test/${id}` })

describe('sending files', () => {
  // The server is told which uploads belong to the message and nothing about what they are: the name, the type, the size and the key are
  // inside the sealed message.
  it('sends a message of only files, naming each upload and nothing else about it, and sealing what each file is', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(wire(message({ id: 'real', content: '' }))))

    await sendMessage('c1', '   ', [upload(1), upload(2)])

    expect(post.mock.calls[0][1]).toEqual({
      ciphertext: sealed('', [upload(1).ref, upload(2).ref]),
      clientMessageId: expect.stringMatching(/^[0-9a-f-]{36}$/),
      mentionedUserIds: [],
      attachments: [{ uploadKey: 'pending/c1/me/key-1' }, { uploadKey: 'pending/c1/me/key-2' }],
    })
    expect(JSON.stringify((post.mock.calls[0][1] as { attachments: unknown }).attachments)).not.toContain('file 1.png')
  })

  it('sends the text together with the files', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(wire(message({ id: 'real' }))))

    await sendMessage('c1', ' look ', [upload(1)])

    expect(post.mock.calls[0][1]).toMatchObject({ ciphertext: sealed('look', [upload(1).ref]), attachments: [{ uploadKey: 'pending/c1/me/key-1' }] })
  })

  it('does not send a message with neither text nor files', async () => {
    const post = vi.spyOn(api, 'post')

    await sendMessage('c1', '  ', [])

    expect(post).not.toHaveBeenCalled()
    expect(store().pending.c1).toBeUndefined()
  })

  it('keeps the files on the unconfirmed message so they can be drawn while it is sending', async () => {
    const response = deferred<AxiosResponse<unknown>>()
    vi.spyOn(api, 'post').mockReturnValue(response.promise)

    const sending = sendMessage('c1', '', [upload(1), upload(2)])

    expect(store().pending.c1[0].attachments.map((a) => a.ref.name)).toEqual(['file 1.png', 'file 2.png'])
    response.resolve(respond(wire(message({ id: 'real', clientMessageId: store().pending.c1[0].clientMessageId }))))
    await sending
  })

  it('replaces the unconfirmed message with the server\'s, which carries the files with their links and what the sealed message says they are', async () => {
    vi.spyOn(api, 'post').mockImplementation((_url, body) =>
      Promise.resolve(
        respond(
          wire(message({
            id: 'real',
            content: '',
            clientMessageId: (body as { clientMessageId: string }).clientMessageId,
            attachments: [{ ...stored('a1', 1), file: upload(1).ref }],
          })),
        ),
      ),
    )

    await sendMessage('c1', '', [upload(1)])

    expect(store().pending.c1).toEqual([])
    expect(store().messages.c1[0].attachments[0]).toMatchObject({ url: 'https://files.test/a1', file: upload(1).ref })
  })

  it('does not vouch for a file the server stored in another size than the upload was', async () => {
    vi.spyOn(api, 'post').mockImplementation((_url, body) =>
      Promise.resolve(
        respond(
          wire(message({
            id: 'real',
            content: '',
            clientMessageId: (body as { clientMessageId: string }).clientMessageId,
            attachments: [{ id: 'a1', sizeBytes: 5, url: 'https://files.test/a1', file: null }],
          })),
        ),
      ),
    )

    await sendMessage('c1', '', [upload(1)])

    expect(store().messages.c1[0].attachments[0].file).toBeNull()
  })

  it('seals the same entries again when the message is sent again, and the same keys', async () => {
    const post = vi.spyOn(api, 'post').mockRejectedValueOnce(failure(500, 'boom')).mockResolvedValue(respond(wire(message({ id: 'real' }))))
    await sendMessage('c1', 'text', [upload(1)])
    const failed = store().pending.c1[0]

    await retryMessage('c1', failed.clientMessageId)

    expect(post.mock.calls[1][1]).toMatchObject({ ciphertext: sealed('text', [upload(1).ref]), clientMessageId: failed.clientMessageId })
  })

  it('shows the failure and keeps the uploads, so the message can be sent again as it was', async () => {
    vi.spyOn(api, 'post').mockRejectedValue(failure(400, 'One of the files was not uploaded.'))

    await sendMessage('c1', 'text', [upload(1)])

    expect(store().pending.c1[0]).toMatchObject({ status: 'failed', error: 'One of the files was not uploaded.' })
    expect(store().pending.c1[0].attachments).toHaveLength(1)
  })

  it('sends the same uploads again with the same id when the message is retried', async () => {
    const post = vi.spyOn(api, 'post').mockRejectedValueOnce(failure(500, 'x')).mockResolvedValue(respond(wire(message({ id: 'real' }))))
    await sendMessage('c1', 'text', [upload(1), upload(2)])
    const failed = store().pending.c1[0]

    await retryMessage('c1', failed.clientMessageId)

    expect(post.mock.calls[1][1]).toEqual(post.mock.calls[0][1])
    expect(post.mock.calls[1][1]).toMatchObject({
      clientMessageId: failed.clientMessageId,
      attachments: [{ uploadKey: 'pending/c1/me/key-1' }, { uploadKey: 'pending/c1/me/key-2' }],
    })
  })

  it('treats the stored message returned for a repeat as the confirmation when it has files', async () => {
    const failed = await failedFileMessage()
    vi.spyOn(api, 'post').mockResolvedValue({
      data: message({ id: 'stored', content: '', clientMessageId: failed.clientMessageId }),
      status: 200,
    } as AxiosResponse<Message>)

    await retryMessage('c1', failed.clientMessageId)

    expect(store().pending.c1).toEqual([])
    expect(store().messages.c1.map((m) => m.id)).toEqual(['stored'])
  })

  async function failedFileMessage() {
    vi.spyOn(api, 'post').mockRejectedValueOnce(failure(500, 'x'))
    await sendMessage('c1', '', [upload(1)])
    return store().pending.c1[0]
  }
})

describe('renewing a link to a file', () => {
  const withFile = () =>
    store().seedMessages('c1', [
      message({
        id: 'm1',
        attachments: [{ id: 'a1', sizeBytes: 1, url: 'https://files.test/old', file: null }],
      }),
    ])

  it('asks the server for a new link and puts it into the message', async () => {
    withFile()
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond({ url: 'https://files.test/new', expiresAt: '2026-10-22T12:00:00Z' }))

    const url = await refreshAttachmentUrl('c1', 'm1', 'a1')

    expect(get.mock.calls[0][0]).toBe('/attachments/a1/url')
    expect(url).toBe('https://files.test/new')
    expect(store().messages.c1[0].attachments[0].url).toBe('https://files.test/new')
  })

  it('leaves the message as it was, and says why, when the server refuses', async () => {
    withFile()
    vi.spyOn(api, 'get').mockRejectedValue(failure(404, 'Attachment not found.'))

    await expect(refreshAttachmentUrl('c1', 'm1', 'a1')).rejects.toThrow()

    expect(store().messages.c1[0].attachments[0].url).toBe('https://files.test/old')
  })
})

// ##### Muting #####

describe('muting a conversation', () => {
  it('puts the choice to the server, and applies it once the server has stored it', async () => {
    store().seedConversations([conversation('c1')])
    const put = vi.spyOn(api, 'put').mockResolvedValue(respond(undefined))

    await setMuted('c1', true)

    expect(put).toHaveBeenCalledWith('/conversations/c1/mute', { isMuted: true })
    expect(store().conversations.c1.isMuted).toBe(true)
  })

  it('does not apply it before the server has answered', async () => {
    store().seedConversations([conversation('c1')])
    const response = deferred<AxiosResponse>()
    vi.spyOn(api, 'put').mockReturnValue(response.promise)

    const pending = setMuted('c1', true)
    await flush()
    expect(store().conversations.c1.isMuted).toBe(false)

    response.resolve(respond(undefined))
    await pending
    expect(store().conversations.c1.isMuted).toBe(true)
  })

  it('leaves the choice as it was when the server refuses, and lets the caller show why', async () => {
    store().seedConversations([conversation('c1')])
    vi.spyOn(api, 'put').mockRejectedValue(failure(403, 'You are not a member of this conversation.'))

    await expect(setMuted('c1', true)).rejects.toThrow()

    expect(store().conversations.c1.isMuted).toBe(false)
  })

  it('unmutes with the same call', async () => {
    store().seedConversations([{ ...conversation('c1'), isMuted: true }])
    const put = vi.spyOn(api, 'put').mockResolvedValue(respond(undefined))

    await setMuted('c1', false)

    expect(put).toHaveBeenCalledWith('/conversations/c1/mute', { isMuted: false })
    expect(store().conversations.c1.isMuted).toBe(false)
  })
})
