import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { HISTORY_PAGE_SIZE } from '../history'
import { useChatStore } from '../store/chatStore'
import type { Message } from '../types'
import { CATCH_UP_MAX_PAGES, catchUp, catchUpAll } from './catchUp'

const store = () => useChatStore.getState()

// ##### Helpers #####

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => {
    resolve = res
  })
  return { promise, resolve }
}

// Message number n of a conversation. Ids and times both grow with n, so the order is unambiguous.
function message(n: number, conversationId = 'c1'): Message {
  const seconds = String(n % 60).padStart(2, '0')
  const minutes = String(Math.floor(n / 60) % 60).padStart(2, '0')
  const hours = String(Math.floor(n / 3600)).padStart(2, '0')
  return {
    id: `m${String(n).padStart(6, '0')}`,
    conversationId,
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: `message ${n}`,
    createdAt: `2026-10-11T${hours}:${minutes}:${seconds}Z`,
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
  }
}

const range = (from: number, to: number, conversationId = 'c1') =>
  Array.from({ length: to - from }, (_, i) => message(from + i, conversationId))

// Message n as the server has it after the sender corrected it once, and after it was deleted.
const edited = (n: number, content = `message ${n} corrected`): Message => ({
  ...message(n),
  content,
  editCount: 1,
  editedAt: '2026-10-11T12:00:00Z',
})

const deleted = (n: number): Message => ({ ...message(n), content: null, isDeleted: true })

const ids = (conversationId: string) => (store().messages[conversationId] ?? []).map((m) => m.id)

const held = (n: number) => store().messages.c1.find((m) => m.id === message(n).id)!

interface PageRequest {
  before?: string
  after?: string
}

// Answers like the history endpoint, from the history it is given (oldest first): the latest page, the page just
// before a message, or the page just after one. Remembers what was asked.
function server(history: Message[]) {
  const requests: PageRequest[] = []
  const get = vi.spyOn(api, 'get').mockImplementation(async (_url, config) => {
    const { before, after } = (config?.params ?? {}) as PageRequest
    requests.push({ before, after })

    if (after !== undefined) return respond(history.filter((m) => m.id > after).slice(0, HISTORY_PAGE_SIZE))

    const older = before === undefined ? history : history.filter((m) => m.id < before)
    return respond(older.slice(-HISTORY_PAGE_SIZE))
  })
  return { requests, get }
}

const noReset = () => ({ onTooFarBehind: vi.fn() })

beforeEach(() => {
  store().reset()
  store().setConnection('connected')
})

afterEach(() => {
  vi.restoreAllMocks()
})

// ##### One conversation #####

describe('catching up on a conversation', () => {
  it('asks for the latest page, which shows both what is new and how the last messages are now', async () => {
    store().seedMessages('c1', range(0, 5))
    const { requests } = server(range(0, 8))

    await catchUp('c1', noReset())

    expect(requests).toEqual([{}])
  })

  it('needs no other request when that page reaches back to the newest message it holds', async () => {
    store().seedMessages('c1', range(0, 5))
    const { get } = server(range(0, 8))

    await catchUp('c1', noReset())

    expect(get).toHaveBeenCalledTimes(1)
  })

  it('puts what it was missing into the store in order', async () => {
    store().seedMessages('c1', range(0, 5))
    server(range(0, 8))

    await catchUp('c1', noReset())

    expect(ids('c1')).toEqual(range(0, 8).map((m) => m.id))
  })

  it('reaches as far as a page minus one new message with the one request', async () => {
    store().seedMessages('c1', range(0, 5))
    const missed = HISTORY_PAGE_SIZE - 1
    const { get } = server(range(0, 5 + missed))

    await catchUp('c1', noReset())

    expect(get).toHaveBeenCalledTimes(1)
    expect(ids('c1')).toHaveLength(5 + missed)
  })

  it('shows an edit made to a message it holds while it was away', async () => {
    store().seedMessages('c1', range(0, 5))
    server([...range(0, 3), edited(3), message(4), ...range(5, 7)])

    await catchUp('c1', noReset())

    expect(held(3)).toMatchObject({ content: 'message 3 corrected', editCount: 1 })
  })

  it('shows a deletion made to a message it holds while it was away', async () => {
    store().seedMessages('c1', range(0, 5))
    server([...range(0, 2), deleted(2), ...range(3, 7)])

    await catchUp('c1', noReset())

    expect(held(2)).toMatchObject({ content: null, isDeleted: true })
  })

  it('shows a change to the newest message it holds, which is the one the new messages follow', async () => {
    store().seedMessages('c1', range(0, 5))
    server([...range(0, 4), deleted(4), ...range(5, 7)])

    await catchUp('c1', noReset())

    expect(held(4).isDeleted).toBe(true)
  })

  it('does not undo a later edit that a push delivered while the page was on its way', async () => {
    store().seedMessages('c1', [...range(0, 3), edited(3, 'second correction'), message(4)])
    store().replaceMessage({ ...edited(3, 'second correction'), editCount: 2 })
    server([...range(0, 3), edited(3, 'first correction'), message(4)])

    await catchUp('c1', noReset())

    expect(held(3)).toMatchObject({ content: 'second correction', editCount: 2 })
  })

  it('leaves a message alone that the page does not mention', async () => {
    store().seedMessages('c1', range(0, 5))
    server(range(2, 8))

    await catchUp('c1', noReset())

    expect(ids('c1')).toEqual(range(0, 8).map((m) => m.id))
  })

  it('makes no request for a message list it has nothing to anchor on, and starts that conversation over instead', async () => {
    store().seedMessages('c1', [])
    const { get } = server([])
    const options = noReset()

    await catchUp('c1', options)

    expect(get).not.toHaveBeenCalled()
    expect(options.onTooFarBehind).toHaveBeenCalledWith('c1')
  })

  it('does not start the conversation over when the gap was filled', async () => {
    store().seedMessages('c1', range(0, 5))
    server(range(0, 8))
    const options = noReset()

    await catchUp('c1', options)

    expect(options.onTooFarBehind).not.toHaveBeenCalled()
  })
})

// ##### A gap longer than the latest page reaches back over #####

describe('a gap that the latest page does not reach back over', () => {
  it('asks for what came after the newest message it holds, then looks back over the last messages it held', async () => {
    store().seedMessages('c1', range(0, 5))
    const { requests } = server(range(0, 5 + HISTORY_PAGE_SIZE))

    await catchUp('c1', noReset())

    // The latest page holds only new messages, so the gap is read from the newest one held. The page after that is empty
    // (the last one was full and happened to be the end), and the first new message tells where to look back from.
    expect(requests).toEqual([
      {},
      { after: message(4).id },
      { after: message(4 + HISTORY_PAGE_SIZE).id },
      { before: message(5).id },
    ])
    expect(ids('c1')).toEqual(range(0, 5 + HISTORY_PAGE_SIZE).map((m) => m.id))
  })

  it('keeps asking, from the newest message of each page, while pages come back full', async () => {
    store().seedMessages('c1', range(0, 5))
    const first = range(5, 5 + HISTORY_PAGE_SIZE)
    const second = range(5 + HISTORY_PAGE_SIZE, 5 + 2 * HISTORY_PAGE_SIZE)
    const { requests } = server(range(0, 5 + 2 * HISTORY_PAGE_SIZE + 3))

    await catchUp('c1', noReset())

    expect(requests).toEqual([
      {},
      { after: message(4).id },
      { after: first[first.length - 1].id },
      { after: second[second.length - 1].id },
      { before: message(5).id },
    ])
    expect(ids('c1')).toHaveLength(5 + 2 * HISTORY_PAGE_SIZE + 3)
  })

  it('does not put the latest page in the store before the gap is filled, so a failure leaves no hole in the history', async () => {
    store().seedMessages('c1', range(0, 5))
    const history = range(0, 5 + 3 * HISTORY_PAGE_SIZE)
    vi.spyOn(api, 'get').mockImplementation(async (_url, config) => {
      if ((config?.params as PageRequest).after !== undefined) throw new AxiosError('Network Error')
      return respond(history.slice(-HISTORY_PAGE_SIZE))
    })

    await expect(catchUp('c1', noReset())).rejects.toThrow()

    expect(ids('c1')).toEqual(range(0, 5).map((m) => m.id))
  })

  it('shows an edit made to one of the last messages it held', async () => {
    store().seedMessages('c1', range(0, 5))
    server([...range(0, 3), edited(3), message(4), ...range(5, 5 + 2 * HISTORY_PAGE_SIZE)])

    await catchUp('c1', noReset())

    expect(held(3)).toMatchObject({ content: 'message 3 corrected', editCount: 1 })
  })

  it('shows a deletion made to one of the last messages it held', async () => {
    store().seedMessages('c1', range(0, 5))
    server([...range(0, 2), deleted(2), ...range(3, 5 + 2 * HISTORY_PAGE_SIZE)])

    await catchUp('c1', noReset())

    expect(held(2)).toMatchObject({ content: null, isDeleted: true })
  })

  it('looks back from the first new message, so what it held and what is new meet with nothing missing between', async () => {
    store().seedMessages('c1', range(0, 80))
    server(range(0, 80 + 3 * HISTORY_PAGE_SIZE))

    await catchUp('c1', noReset())

    expect(ids('c1')).toEqual(range(0, 80 + 3 * HISTORY_PAGE_SIZE).map((m) => m.id))
  })
})

// ##### A gap that is too long #####

describe('a gap that is too long to fill', () => {
  const tooLong = () => server(range(0, 5 + (CATCH_UP_MAX_PAGES + 1) * HISTORY_PAGE_SIZE))

  it('stops after the page limit instead of reading for ever', async () => {
    store().seedMessages('c1', range(0, 5))
    const { get } = tooLong()

    await catchUp('c1', noReset())

    // The latest page, then one request for each page of the gap.
    expect(get).toHaveBeenCalledTimes(1 + CATCH_UP_MAX_PAGES)
  })

  it('forgets what it holds of the conversation and asks for it to be loaded afresh', async () => {
    store().seedMessages('c1', range(0, 5))
    tooLong()
    const options = noReset()

    await catchUp('c1', options)

    expect(options.onTooFarBehind).toHaveBeenCalledTimes(1)
    expect(options.onTooFarBehind).toHaveBeenCalledWith('c1')
    expect(store().messages.c1).toBeUndefined()
  })

  it('does not look back over what it held, since it forgot it', async () => {
    store().seedMessages('c1', range(0, 5))
    const { requests } = tooLong()

    await catchUp('c1', noReset())

    expect(requests.filter((r) => r.before !== undefined)).toEqual([])
  })

  it('does not count a last page that was short as too long', async () => {
    store().seedMessages('c1', range(0, 5))
    const lastStart = 5 + (CATCH_UP_MAX_PAGES - 1) * HISTORY_PAGE_SIZE
    server(range(0, lastStart + 10))
    const options = noReset()

    await catchUp('c1', options)

    expect(options.onTooFarBehind).not.toHaveBeenCalled()
    expect(ids('c1')).toHaveLength(lastStart + 10)
  })

  it('keeps messages the reader sent that the server has not confirmed', async () => {
    store().seedMessages('c1', range(0, 5))
    store().addPending({
      clientMessageId: 'p1',
      conversationId: 'c1',
      content: 'still sending',
      attachments: [],
      createdAt: '2026-10-11T10:00:00Z',
      status: 'sending',
      error: null,
    })
    tooLong()

    await catchUp('c1', noReset())

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p1'])
  })
})

// ##### The connection dropping again #####

describe('catching up while another catch-up is running', () => {
  it('shares the running one instead of starting a second', async () => {
    store().seedMessages('c1', range(0, 5))
    const reply = deferred<AxiosResponse<Message[]>>()
    const get = vi.spyOn(api, 'get').mockReturnValueOnce(reply.promise).mockResolvedValue(respond(range(0, 8)))

    const first = catchUp('c1', noReset())
    const second = catchUp('c1', noReset())
    reply.resolve(respond(range(0, 8)))
    await Promise.all([first, second])

    // One loop ran, and looked a second time because it was asked again while running.
    expect(first).toBe(second)
    expect(get).toHaveBeenCalledTimes(2)
  })

  it('looks once more afterwards, because messages may have been sent after its last request was answered', async () => {
    store().seedMessages('c1', range(0, 5))
    const reply = deferred<AxiosResponse<Message[]>>()
    const asked: PageRequest[] = []
    vi.spyOn(api, 'get').mockImplementation((_url, config) => {
      asked.push((config?.params ?? {}) as PageRequest)
      return asked.length === 1 ? reply.promise : Promise.resolve(respond(range(0, 10)))
    })

    const running = catchUp('c1', noReset())
    await Promise.resolve()
    const joined = catchUp('c1', noReset())
    reply.resolve(respond(range(0, 8)))
    await Promise.all([running, joined])

    expect(asked).toEqual([{}, {}])
    expect(ids('c1')).toEqual(range(0, 10).map((m) => m.id))
  })

  it('does not look again when nobody asked during the run', async () => {
    store().seedMessages('c1', range(0, 5))
    const { get } = server(range(0, 8))

    await catchUp('c1', noReset())

    expect(get).toHaveBeenCalledTimes(1)
  })

  it('can run again after one that failed', async () => {
    store().seedMessages('c1', range(0, 5))
    vi.spyOn(api, 'get').mockRejectedValueOnce(new AxiosError('Network Error')).mockResolvedValue(respond(range(0, 7)))

    await expect(catchUp('c1', noReset())).rejects.toThrow()
    await catchUp('c1', noReset())

    expect(ids('c1')).toEqual(range(0, 7).map((m) => m.id))
  })

  it('leaves the store as it was when the request fails', async () => {
    store().seedMessages('c1', range(0, 5))
    vi.spyOn(api, 'get').mockRejectedValue(new AxiosError('Network Error'))

    await expect(catchUp('c1', noReset())).rejects.toThrow()

    expect(ids('c1')).toEqual(range(0, 5).map((m) => m.id))
  })
})

// ##### Every conversation #####

describe('catching up on all conversations', () => {
  it('visits each conversation that has been loaded and no other', async () => {
    store().seedMessages('c1', range(0, 3, 'c1'))
    store().seedMessages('c2', range(0, 3, 'c2'))
    store().seedConversations([
      { id: 'c3', type: 'Direct', name: 'Never opened', avatarUrl: null, updatedAt: '2026-10-11T09:00:00Z', lastMessage: null, unreadCount: 0, otherUserId: null, isMuted: false, leftAt: null },
    ])
    const urls: string[] = []
    vi.spyOn(api, 'get').mockImplementation(async (url) => {
      urls.push(url)
      return respond([])
    })

    await catchUpAll(noReset())

    expect(urls.sort()).toEqual(['/conversations/c1/messages', '/conversations/c2/messages'])
  })

  it('carries on with the others when one conversation fails', async () => {
    store().seedMessages('c1', range(0, 3, 'c1'))
    store().seedMessages('c2', range(0, 3, 'c2'))
    vi.spyOn(api, 'get').mockImplementation(async (url) => {
      if (url.includes('/c1/')) throw new AxiosError('Network Error')
      return respond(range(0, 5, 'c2'))
    })

    await expect(catchUpAll(noReset())).resolves.toBeUndefined()

    expect(ids('c2')).toHaveLength(5)
    expect(ids('c1')).toHaveLength(3)
  })

  it('never has more than four requests in flight', async () => {
    for (let i = 0; i < 10; i++) store().seedMessages(`c${i}`, range(0, 2, `c${i}`))
    let inFlight = 0
    let peak = 0
    vi.spyOn(api, 'get').mockImplementation(async () => {
      inFlight++
      peak = Math.max(peak, inFlight)
      await new Promise((resolve) => setTimeout(resolve, 5))
      inFlight--
      return respond([])
    })

    await catchUpAll(noReset())

    expect(peak).toBe(4)
  })

  it('does nothing when no conversation has been loaded', async () => {
    const get = vi.spyOn(api, 'get')

    await catchUpAll(noReset())

    expect(get).not.toHaveBeenCalled()
  })
})
