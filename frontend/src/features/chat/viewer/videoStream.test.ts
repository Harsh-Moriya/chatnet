import { afterEach, describe, expect, it, vi } from 'vitest'
import { toBase64Url } from '../../../lib/crypto/bytes'
import { encryptedFileLength } from '../../../lib/crypto/file'
import type { FileRef } from '../../../lib/crypto/message'
import type { StreamDescriptor } from '../../../sw/protocol'
import type { FileSource } from '../files/fileObjects'
import { type StreamHost, createVideoStreams } from './videoStream'

const ID = '7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'
const KEY = Uint8Array.from({ length: 32 }, (_, i) => i + 1)

const entry = (type = 'video/webm'): FileRef => ({ name: 'clip.webm', type, size: 1_000_000, key: toBase64Url(KEY), chunkSize: 65536 })
const source = (type?: string): FileSource => ({ id: 'a1', conversationId: 'c1', messageId: 'm1', url: 'https://bucket.test/f?sig=1', entry: entry(type) })

// A page and a worker with a real channel between them: what the page posts reaches `received`, and the worker's questions come in by `ask`.
function setup(options: { controller?: boolean; ack?: { ok: boolean } | 'silent'; renew?: () => Promise<string> } = {}) {
  const { controller = true, ack = { ok: true }, renew = async () => 'https://bucket.test/f?sig=2' } = options
  const received: { message: { type: string; stream?: StreamDescriptor; id?: string }; transfer: Transferable[] }[] = []
  let listener: ((message: { data: unknown; ports: readonly { postMessage: (reply: unknown) => void }[] }) => void) | null = null
  let listening = 0
  let stopped = 0

  const host: StreamHost = {
    controller: () =>
      controller
        ? {
            postMessage: (message, transfer) => {
              received.push({ message: message as never, transfer })
              if (ack !== 'silent' && (message as { type: string }).type === 'stream-open') (transfer[0] as MessagePort).postMessage(ack)
            },
          }
        : null,
    listen: (l) => {
      listener = l
      listening++
      return () => {
        listener = null
        stopped++
      }
    },
    renewLink: vi.fn(renew),
    newId: () => ID,
  }

  // The worker asks something and the page answers on the port: the answer is what comes back.
  const ask = (data: unknown) =>
    new Promise<unknown>((resolve) => {
      const channel = new MessageChannel()
      channel.port1.onmessage = (event) => {
        resolve(event.data)
        channel.port1.close()
      }
      listener?.({ data, ports: [channel.port2] })
      setTimeout(() => resolve('no answer'), 100)
    })

  return { host, received, ask, streams: createVideoStreams(host), state: () => ({ listening, stopped, listener }) }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('opening a stream', () => {
  it('tells the worker the key, how the file is cut and how large it is as it was and as it is stored, and where it is', async () => {
    const page = setup()

    await page.streams.open(source())

    const { message } = page.received[0]
    expect(message.type).toBe('stream-open')
    expect(message.stream).toMatchObject({
      id: ID,
      chunkSize: 65536,
      size: 1_000_000,
      storedSize: encryptedFileLength(1_000_000, 65536),
      url: 'https://bucket.test/f?sig=1',
      contentType: 'video/webm',
    })
    expect(Array.from(message.stream!.key)).toEqual(Array.from(KEY))
  })

  it('gives the address to play from, once the worker has taken the stream', async () => {
    const stream = await setup().streams.open(source())

    expect(stream?.url).toBe(`/file-stream/${ID}`)
  })

  it('gives the worker the type a video has, whatever case or parameters the sender wrote', async () => {
    const page = setup()

    await page.streams.open(source('Video/WebM; codecs=vp8'))

    expect(page.received[0].message.stream!.contentType).toBe('video/webm')
  })

  it('gives nothing when there is no worker that controls the page, and tells nobody', async () => {
    const page = setup({ controller: false })

    expect(await page.streams.open(source())).toBeNull()
    expect(page.received).toEqual([])
    expect(page.state().listening).toBe(0)
  })

  it.each(['image/png', 'application/pdf', 'video/x-msvideo', ''])('gives nothing for a file that says it is %j', async (type) => {
    const page = setup()

    expect(await page.streams.open(source(type))).toBeNull()
    expect(page.received).toEqual([])
  })

  it('gives nothing, and forgets the stream and stops listening, when the worker did not take it', async () => {
    const page = setup({ ack: { ok: false } })

    expect(await page.streams.open(source())).toBeNull()
    expect(page.state()).toMatchObject({ listening: 1, stopped: 1, listener: null })
  })

  it('gives nothing when the worker does not answer in time', async () => {
    vi.useFakeTimers()
    const page = setup({ ack: 'silent' })

    const opening = page.streams.open(source())
    await vi.advanceTimersByTimeAsync(4000)

    expect(await opening).toBeNull()
  })
})

describe('when the worker asks for the stream again', () => {
  it('gives it as it was, key included, because the browser stopped the worker and it lost what it had', async () => {
    const page = setup()
    await page.streams.open(source())

    const answer = (await page.ask({ type: 'stream-need', id: ID })) as StreamDescriptor

    expect(answer).toMatchObject({ id: ID, size: 1_000_000, contentType: 'video/webm' })
    expect(Array.from(answer.key)).toEqual(Array.from(KEY))
  })

  it('says it has nothing for a stream it does not know', async () => {
    const page = setup()
    await page.streams.open(source())

    expect(await page.ask({ type: 'stream-need', id: '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d' })).toBeNull()
  })

  it.each([null, 'text', 7, {}, { type: 'stream-need' }, { type: 'stream-need', id: 5 }, { type: 'other', id: ID }])('does not answer what is not a question: %j', async (data) => {
    const page = setup()
    await page.streams.open(source())

    expect(await page.ask(data)).toBe('no answer')
  })
})

describe('when the link has expired', () => {
  it('asks the API for a new one and gives it to the worker', async () => {
    const page = setup()
    await page.streams.open(source())

    const answer = await page.ask({ type: 'stream-renew', id: ID })

    expect(answer).toEqual({ url: 'https://bucket.test/f?sig=2' })
    expect(page.host.renewLink).toHaveBeenCalledExactlyOnceWith(source())
  })

  it('keeps the new link with the stream, so that a worker that asks for the stream again is given it', async () => {
    const page = setup()
    await page.streams.open(source())

    await page.ask({ type: 'stream-renew', id: ID })
    const again = (await page.ask({ type: 'stream-need', id: ID })) as StreamDescriptor

    expect(again.url).toBe('https://bucket.test/f?sig=2')
  })

  it('says it has none when the API could not give one', async () => {
    const page = setup({ renew: async () => Promise.reject(new Error('offline')) })
    await page.streams.open(source())

    expect(await page.ask({ type: 'stream-renew', id: ID })).toBeNull()
  })

  it('says it has none for a stream it does not know, without asking the API', async () => {
    const page = setup()

    expect(await page.ask({ type: 'stream-renew', id: ID })).toBe('no answer')
    expect(page.host.renewLink).not.toHaveBeenCalled()
  })
})

describe('stopping', () => {
  it('lets the worker forget the stream, once', async () => {
    const page = setup()
    const stream = (await page.streams.open(source()))!

    stream.stop()
    stream.stop()

    expect(page.received.filter(({ message }) => message.type === 'stream-close')).toEqual([{ message: { type: 'stream-close', id: ID }, transfer: [] }])
  })

  it('has nothing to answer about it afterwards, and stops listening when it was the last stream', async () => {
    const page = setup()
    const stream = (await page.streams.open(source()))!

    expect(page.state().listening).toBe(1)
    stream.stop()

    expect(page.state().stopped).toBe(1)
  })
})
