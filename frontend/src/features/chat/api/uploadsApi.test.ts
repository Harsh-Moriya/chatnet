import axios, { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { encryptedFileLength } from '../../../lib/crypto/file'
import { UploadFailed, fetchAttachmentLink, uploadFile } from './uploadsApi'

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

const TICKET = {
  uploadKey: 'pending/c1/me/key',
  url: 'https://files.test/chatnet/pending/c1/me/key?X-Amz-Signature=abc',
  expiresAt: '2026-10-22T12:00:00Z',
}

const PLAIN = Uint8Array.from({ length: 2048 }, (_, i) => (i * 7) % 251)

// A real file: it is read and encrypted for real, so what is sent can be opened again with the key the message will carry.
const photo = () => new File([PLAIN], 'photo.png', { type: 'image/png' })

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
  it('asks the API for permission, telling it how large the encrypted file is and nothing about what the file is', async () => {
    const post = ticketGranted()
    storageAccepts()

    await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    expect(post).toHaveBeenCalledWith('/conversations/c1/uploads', { sizeBytes: encryptedFileLength(2048) }, expect.anything())
  })

  it('sends the encrypted file, as an opaque object, to the address it was given', async () => {
    ticketGranted()
    const put = storageAccepts()

    await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    const sent = put.mock.calls[0][1] as Uint8Array
    expect(put.mock.calls[0][0]).toBe(TICKET.url)
    expect(put.mock.calls[0][2]).toMatchObject({ headers: { 'Content-Type': 'application/octet-stream' } })
    expect(sent).toHaveLength(encryptedFileLength(2048))
    expect(Array.from(sent.slice(0, 2))).toEqual([1, 1])
  })

  it('does not put the bytes of the file, or its name, into what is sent', async () => {
    ticketGranted()
    const put = storageAccepts()

    await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    const sent = Buffer.from(put.mock.calls[0][1] as Uint8Array)
    expect(sent.includes(Buffer.from(PLAIN.slice(0, 64)))).toBe(false)
    expect(sent.includes(Buffer.from('photo.png'))).toBe(false)
  })

  it("sends it with the plain HTTP client, so the API's token never goes to the bucket", async () => {
    ticketGranted()
    storageAccepts()
    const apiPut = vi.spyOn(api, 'put')

    await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    expect(apiPut).not.toHaveBeenCalled()
  })

  it('returns the key the message will name and the entry that says what the file is and how to open it', async () => {
    ticketGranted()
    storageAccepts()

    const { uploadKey, ref } = await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    expect(uploadKey).toBe('pending/c1/me/key')
    expect(ref).toMatchObject({ name: 'photo.png', type: 'image/png', size: 2048, chunkSize: 65536 })
    expect(ref.key).toMatch(/^[A-Za-z0-9_-]{43}$/)
  })

  it('gives an entry whose key opens exactly the file that was sent', async () => {
    ticketGranted()
    const put = storageAccepts()

    const { ref } = await uploadFile('c1', photo(), () => {}, new AbortController().signal)
    const { openAttachment } = await import('../../../lib/crypto/attachment')

    expect(Array.from(await openAttachment(ref, put.mock.calls[0][1] as Uint8Array))).toEqual(Array.from(PLAIN))
  })

  it('makes a key of its own for every file', async () => {
    ticketGranted()
    storageAccepts()

    const one = await uploadFile('c1', photo(), () => {}, new AbortController().signal)
    const two = await uploadFile('c1', photo(), () => {}, new AbortController().signal)

    expect(one.ref.key).not.toBe(two.ref.key)
  })

  it('makes the name harmless before it goes into the entry', async () => {
    ticketGranted()
    storageAccepts()
    const file = new File([PLAIN], 'C:\\Users\\x\\evil\u202Egnp.exe', { type: 'image/png' })

    const { ref } = await uploadFile('c1', file, () => {}, new AbortController().signal)

    expect(ref.name).toBe('evilgnp.exe')
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

  it("does not send the file when the API refuses permission, and lets the API's reason through", async () => {
    const refusal = new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
      status: 400,
      data: { detail: 'This file is larger than the limit.' },
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
