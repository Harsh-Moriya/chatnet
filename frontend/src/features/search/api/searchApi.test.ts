import type { AxiosResponse } from 'axios'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import type { SearchHit } from '../types'
import { searchMessages } from './searchApi'

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

afterEach(() => {
  vi.restoreAllMocks()
})

const hits: SearchHit[] = [
  {
    messageId: 'm1',
    conversationId: 'c1',
    conversationName: 'Bob',
    conversationType: 'Direct',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    createdAt: '2026-10-11T10:00:00Z',
    snippet: [{ text: 'a needle', match: true }],
    fileName: null,
  },
]

describe('searching messages', () => {
  it('asks for the first page with the text and no cursor, and returns the hits as they came', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(hits))

    const found = await searchMessages('needle')

    expect(get).toHaveBeenCalledWith('/search/messages', { params: { q: 'needle', before: undefined }, signal: undefined })
    expect(found).toBe(hits)
  })

  it('continues after the last hit of the page before, and can be cancelled', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond<SearchHit[]>([]))
    const signal = new AbortController().signal

    await searchMessages('needle', 'm1', signal)

    expect(get).toHaveBeenCalledWith('/search/messages', { params: { q: 'needle', before: 'm1' }, signal })
  })
})
