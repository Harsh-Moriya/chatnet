import axios, { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { UploadFailed, fetchAttachmentLink, uploadFile } from './uploadsApi'

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

const TICKET = {
  uploadKey: 'pending/c1/me/key',
  url: 'https://files.test/chatnet/pending/c1/me/key?X-Amz-Signature=abc',
  expiresAt: '2026-10-22T12:00:00Z',
}

const photo = () => ({ name: 'photo.png', type: 'image/png', size: 2048 }) as File

afterEach(() => {
  vi.restoreAllMocks()
})

function ticketGranted() {
  return vi.spyOn(api, 'post').mockResolvedValue(respond(TICKET))
}

function storageAccepts() {
  return vi.spyOn(axios, 'put').mockResolvedValue({})
}

describe('uploading a file', () => {
  it('asks the API for permission, naming the file, its type and its size', async () => {
    const post = ticketGranted()
    storageAccepts()

    await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    expect(post).toHaveBeenCalledWith(
      '/conversations/c1/uploads',
      { fileName: 'photo.png', contentType: 'image/png', sizeBytes: 2048 },
      expect.anything(),
    )
  })

  it('sends the file to the address it was given, with exactly the content type it asked for', async () => {
    ticketGranted()
    const put = storageAccepts()
    const file = photo()

    await uploadFile('c1', file, () => {}, new AbortController().signal)

    expect(put).toHaveBeenCalledWith(TICKET.url, file, expect.objectContaining({ headers: { 'Content-Type': 'image/png' } }))
  })

  it('sends it with the plain HTTP client, so the API\'s token never goes to the bucket', async () => {
    ticketGranted()
    storageAccepts()
    const apiPut = vi.spyOn(api, 'put')

    await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    expect(apiPut).not.toHaveBeenCalled()
  })

  it('returns the key the message will name', async () => {
    ticketGranted()
    storageAccepts()

    await expect(uploadFile('c1', photo(), () => {}, new AbortController().signal)).resolves.toBe('pending/c1/me/key')
  })

  it('reports how much has been sent as a fraction', async () => {
    ticketGranted()
    vi.spyOn(axios, 'put').mockImplementation((_url, _data, config) => {
      config?.onUploadProgress?.({ loaded: 512, total: 2048 } as never)
      config?.onUploadProgress?.({ loaded: 2048, total: 2048 } as never)
      return Promise.resolve({})
    })
    const seen: number[] = []

    await uploadFile('c1', photo(), (fraction) => seen.push(fraction), new AbortController().signal)

    expect(seen).toEqual([0.25, 1])
  })

  it('reports no progress rather than a nonsense number when the total is not known', async () => {
    ticketGranted()
    vi.spyOn(axios, 'put').mockImplementation((_url, _data, config) => {
      config?.onUploadProgress?.({ loaded: 512 } as never)
      return Promise.resolve({})
    })
    const seen: number[] = []

    await uploadFile('c1', photo(), (fraction) => seen.push(fraction), new AbortController().signal)

    expect(seen).toEqual([0])
  })

  it('passes the cancel signal to both requests', async () => {
    const post = ticketGranted()
    const put = storageAccepts()
    const controller = new AbortController()

    await uploadFile('c1', photo(), () => {}, controller.signal)

    expect(post.mock.calls[0][2]).toMatchObject({ signal: controller.signal })
    expect(put.mock.calls[0][2]).toMatchObject({ signal: controller.signal })
  })

  it('does not send the file when the API refuses permission, and lets the API\'s reason through', async () => {
    const refusal = new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
      status: 400,
      data: { detail: 'This type of file cannot be sent.' },
    } as AxiosResponse)
    vi.spyOn(api, 'post').mockRejectedValue(refusal)
    const put = storageAccepts()

    await expect(uploadFile('c1', photo(), () => {}, new AbortController().signal)).rejects.toBe(refusal)

    expect(put).not.toHaveBeenCalled()
  })

  it('says the upload failed, in words of its own, when the bucket refuses the file', async () => {
    ticketGranted()
    vi.spyOn(axios, 'put').mockRejectedValue(new AxiosError('Request failed with status code 403'))

    const failure = await uploadFile('c1', photo(), () => {}, new AbortController().signal).catch((e: unknown) => e)

    expect(failure).toBeInstanceOf(UploadFailed)
    expect((failure as Error).message).toContain('could not be uploaded')
    expect((failure as Error).message).not.toContain('403')
  })

  it('says the same when the network drops during the upload', async () => {
    ticketGranted()
    vi.spyOn(axios, 'put').mockRejectedValue(new AxiosError('Network Error'))

    await expect(uploadFile('c1', photo(), () => {}, new AbortController().signal)).rejects.toBeInstanceOf(UploadFailed)
  })

  it('lets a cancelled upload through as a cancellation, not as a failure to show', async () => {
    ticketGranted()
    const cancelled = new axios.CanceledError('canceled')
    vi.spyOn(axios, 'put').mockRejectedValue(cancelled)

    const result = await uploadFile('c1', photo(), () => {}, new AbortController().signal).catch((e: unknown) => e)

    expect(axios.isCancel(result)).toBe(true)
    expect(result).not.toBeInstanceOf(UploadFailed)
  })
})

describe('fetching a new link to a file', () => {
  it('asks for the attachment by id and returns the link', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond({ url: 'https://files.test/new', expiresAt: '2026-10-22T13:00:00Z' }))

    const link = await fetchAttachmentLink('a1')

    expect(get).toHaveBeenCalledWith('/attachments/a1/url')
    expect(link.url).toBe('https://files.test/new')
  })
})
