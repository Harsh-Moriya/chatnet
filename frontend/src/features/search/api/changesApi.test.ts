import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { CURSOR_AHEAD, fetchChanges, fetchHistoryPage, isCursorAhead } from './changesApi'

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

function failure(status: number, data: unknown): AxiosError {
  return new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, { status, data } as AxiosResponse)
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('fetchChanges', () => {
  it('asks the feed of the conversation from the point and returns the page as the server sent it', async () => {
    const page = { changes: [], cursor: 7, more: false }
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(page))
    const signal = new AbortController().signal

    await expect(fetchChanges('c-1', 5, signal)).resolves.toBe(page)

    expect(get).toHaveBeenCalledWith('/conversations/c-1/changes', { params: { since: 5 }, signal })
  })

  it('asks from the start with a point of zero, which is a value and not an absent one', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond({ changes: [], cursor: 0, more: false }))

    await fetchChanges('c-1', 0)

    expect(get).toHaveBeenCalledWith('/conversations/c-1/changes', { params: { since: 0 }, signal: undefined })
  })
})

describe('fetchHistoryPage', () => {
  it('asks for the latest page when there is no point to read before', async () => {
    const messages = [{ id: 'm-1' }]
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond(messages))

    await expect(fetchHistoryPage('c-1', null)).resolves.toBe(messages)

    expect(get).toHaveBeenCalledWith('/conversations/c-1/messages', { params: {}, signal: undefined })
  })

  it('asks for the page before a message when there is one', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))
    const signal = new AbortController().signal

    await fetchHistoryPage('c-1', 'm-9', signal)

    expect(get).toHaveBeenCalledWith('/conversations/c-1/messages', { params: { before: 'm-9' }, signal })
  })
})

describe('isCursorAhead', () => {
  it('is true for the 409 that carries the code of a point past the newest change', () => {
    expect(isCursorAhead(failure(409, { code: CURSOR_AHEAD }))).toBe(true)
  })

  it('is false for another conflict, which is not the server being older than this device', () => {
    expect(isCursorAhead(failure(409, { code: 'epoch-stale' }))).toBe(false)
  })

  it('is false for the code on any other status', () => {
    expect(isCursorAhead(failure(400, { code: CURSOR_AHEAD }))).toBe(false)
    expect(isCursorAhead(failure(500, { code: CURSOR_AHEAD }))).toBe(false)
  })

  it('is false for an answer with no body, and for errors that are not from a request', () => {
    expect(isCursorAhead(failure(409, undefined))).toBe(false)
    expect(isCursorAhead(new Error('offline'))).toBe(false)
    expect(isCursorAhead(null)).toBe(false)
  })
})
