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
  }
}

const range = (from: number, to: number, conversationId = 'c1') =>
  Array.from({ length: to - from }, (_, i) => message(from + i, conversationId))

const ids = (conversationId: string) => (store().messages[conversationId] ?? []).map((m) => m.id)

// Answers each "after" request from a list of pages, in order, and remembers what was asked.
function serverPages(...pages: Message[][]) {
  const asked: Array<string | undefined> = []
  const get = vi.spyOn(api, 'get').mockImplementation(async (_url, config) => {
    const params = config?.params as { before?: string; after?: string } | undefined
    asked.push(params?.after)
    return respond(pages[asked.length - 1] ?? [])
  })
  return { asked, get }
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
  it('asks for what came after the newest message it has', async () => {
    store().seedMessages('c1', range(0, 5))
    const { asked } = serverPages(range(5, 8))

    await catchUp('c1', noReset())

    expect(asked).toEqual([message(4).id])
  })

  it('puts what it was missing into the store in order', async () => {
    store().seedMessages('c1', range(0, 5))
    serverPages(range(5, 8))

    await catchUp('c1', noReset())

    expect(ids('c1')).toEqual(range(0, 8).map((m) => m.id))
  })

  it('stops after one request when the page is short, which means nothing more is missing', async () => {
    store().seedMessages('c1', range(0, 5))
    const { get } = serverPages(range(5, 8))

    await catchUp('c1', noReset())

    expect(get).toHaveBeenCalledTimes(1)
  })

  it('makes no request for a message list it has nothing to anchor on, and starts that conversation over instead', async () => {
    store().seedMessages('c1', [])
    const { get } = serverPages()
    const options = noReset()

    await catchUp('c1', options)

    expect(get).not.toHaveBeenCalled()
    expect(options.onTooFarBehind).toHaveBeenCalledWith('c1')
  })

  it('keeps asking, from the newest message of each page, while pages come back full', async () => {
    store().seedMessages('c1', range(0, 5))
    const first = range(5, 5 + HISTORY_PAGE_SIZE)
    const second = range(5 + HISTORY_PAGE_SIZE, 5 + 2 * HISTORY_PAGE_SIZE)
    const { asked } = serverPages(first, second, range(5 + 2 * HISTORY_PAGE_SIZE, 5 + 2 * HISTORY_PAGE_SIZE + 3))

    await catchUp('c1', noReset())

    expect(asked).toEqual([message(4).id, first[first.length - 1].id, second[second.length - 1].id])
    expect(ids('c1')).toHaveLength(5 + 2 * HISTORY_PAGE_SIZE + 3)
  })

  it('ends on an empty page after a page that was full and happened to be the last', async () => {
    store().seedMessages('c1', range(0, 5))
    const full = range(5, 5 + HISTORY_PAGE_SIZE)
    const { get } = serverPages(full, [])

    await catchUp('c1', noReset())

    expect(get).toHaveBeenCalledTimes(2)
    expect(ids('c1')).toHaveLength(5 + HISTORY_PAGE_SIZE)
  })

  it('does not start the conversation over when the gap was filled', async () => {
    store().seedMessages('c1', range(0, 5))
    serverPages(range(5, 8))
    const options = noReset()

    await catchUp('c1', options)

    expect(options.onTooFarBehind).not.toHaveBeenCalled()
  })
})

// ##### A gap that is too long #####

describe('a gap that is too long to fill', () => {
  function everyPageFull() {
    return vi.spyOn(api, 'get').mockImplementation(async (_url, config) => {
      const after = (config?.params as { after?: string }).after!
      const next = Number(after.slice(1)) + 1
      return respond(range(next, next + HISTORY_PAGE_SIZE))
    })
  }

  it('stops after the page limit instead of reading for ever', async () => {
    store().seedMessages('c1', range(0, 5))
    const get = everyPageFull()

    await catchUp('c1', noReset())

    expect(get).toHaveBeenCalledTimes(CATCH_UP_MAX_PAGES)
  })

  it('forgets what it holds of the conversation and asks for it to be loaded afresh', async () => {
    store().seedMessages('c1', range(0, 5))
    everyPageFull()
    const options = noReset()

    await catchUp('c1', options)

    expect(options.onTooFarBehind).toHaveBeenCalledTimes(1)
    expect(options.onTooFarBehind).toHaveBeenCalledWith('c1')
    expect(store().messages.c1).toBeUndefined()
  })

  it('does not count a last page that was short as too long', async () => {
    store().seedMessages('c1', range(0, 5))
    const pages = Array.from({ length: CATCH_UP_MAX_PAGES - 1 }, (_, i) =>
      range(5 + i * HISTORY_PAGE_SIZE, 5 + (i + 1) * HISTORY_PAGE_SIZE),
    )
    const lastStart = 5 + (CATCH_UP_MAX_PAGES - 1) * HISTORY_PAGE_SIZE
    serverPages(...pages, range(lastStart, lastStart + 10))
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
      createdAt: '2026-10-11T10:00:00Z',
      status: 'sending',
      error: null,
    })
    everyPageFull()

    await catchUp('c1', noReset())

    expect(store().pending.c1.map((p) => p.clientMessageId)).toEqual(['p1'])
  })
})

// ##### The connection dropping again #####

describe('catching up while another catch-up is running', () => {
  it('shares the running one instead of starting a second', async () => {
    store().seedMessages('c1', range(0, 5))
    const reply = deferred<AxiosResponse<Message[]>>()
    const get = vi.spyOn(api, 'get').mockReturnValueOnce(reply.promise).mockResolvedValue(respond([]))

    const first = catchUp('c1', noReset())
    const second = catchUp('c1', noReset())
    reply.resolve(respond(range(5, 8)))
    await Promise.all([first, second])

    // One loop ran, and looked a second time because it was asked again while running.
    expect(first).toBe(second)
    expect(get).toHaveBeenCalledTimes(2)
  })

  it('looks once more afterwards, because messages may have been sent after its last request was answered', async () => {
    store().seedMessages('c1', range(0, 5))
    const reply = deferred<AxiosResponse<Message[]>>()
    const asked: Array<string | undefined> = []
    vi.spyOn(api, 'get').mockImplementation((_url, config) => {
      asked.push((config?.params as { after?: string }).after)
      return asked.length === 1 ? reply.promise : Promise.resolve(respond(range(8, 10)))
    })

    const running = catchUp('c1', noReset())
    await Promise.resolve()
    const joined = catchUp('c1', noReset())
    reply.resolve(respond(range(5, 8)))
    await Promise.all([running, joined])

    expect(asked).toEqual([message(4).id, message(7).id])
    expect(ids('c1')).toEqual(range(0, 10).map((m) => m.id))
  })

  it('does not look again when nobody asked during the run', async () => {
    store().seedMessages('c1', range(0, 5))
    const { get } = serverPages(range(5, 8))

    await catchUp('c1', noReset())

    expect(get).toHaveBeenCalledTimes(1)
  })

  it('can run again after one that failed', async () => {
    store().seedMessages('c1', range(0, 5))
    vi.spyOn(api, 'get').mockRejectedValueOnce(new AxiosError('Network Error')).mockResolvedValue(respond(range(5, 7)))

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
      { id: 'c3', type: 'Direct', name: 'Never opened', avatarUrl: null, updatedAt: '2026-10-11T09:00:00Z', lastMessage: null, unreadCount: 0 },
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
      return respond(range(3, 5, 'c2'))
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
