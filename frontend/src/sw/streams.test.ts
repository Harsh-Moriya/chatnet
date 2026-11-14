import { describe, expect, it, vi } from 'vitest'
import { encryptFile, newFileKey } from '../lib/crypto/file'
import { encryptedFileLength } from '../lib/crypto/file'
import type { StreamDescriptor } from './protocol'
import { MAX_RESPONSE_BYTES } from './range'
import { MAX_STREAMS, type StreamDeps, createRegistry, handleStreamRequest } from './streams'

// A file really sealed, a bucket that answers byte ranges as a real one does, and the page that is asked what the worker has forgotten.

const ID = '7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'
const OTHER_ID = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d'
const CLIENT = 'client-1'

function bytesOf(length: number): Uint8Array {
  const bytes = new Uint8Array(length)
  for (let i = 0; i < length; i++) bytes[i] = (i * 31 + 7) % 251
  return bytes
}

async function sealed(length: number, chunkSize = 64) {
  const key = newFileKey()
  const plain = bytesOf(length)
  const stored = await encryptFile(key, plain, { chunkSize })
  const stream: StreamDescriptor = {
    id: ID,
    key,
    chunkSize,
    size: length,
    storedSize: stored.length,
    url: 'https://bucket.test/file?sig=1',
    contentType: 'video/mp4',
  }
  return { key, plain, stored, stream }
}

// The bytes of an object as a bucket answers a range of it: a 206 with those bytes, cut at the end of the object.
function ranged(object: Uint8Array) {
  return async (_url: string, from: number, to: number) => new Response(object.slice(from, Math.min(to + 1, object.length)), { status: 206 })
}

function deps(overrides: Partial<StreamDeps> & { object: Uint8Array }) {
  const { object, ...rest } = overrides
  return {
    fetchRange: vi.fn(rest.fetchRange ?? ranged(object)),
    askPageForStream: vi.fn(rest.askPageForStream ?? (async () => null)),
    askPageForLink: vi.fn(rest.askPageForLink ?? (async () => null)),
  } satisfies StreamDeps
}

const request = (range: string | null, id = ID) => new Request(`https://chat.test/file-stream/${id}`, { headers: range === null ? {} : { Range: range } })
const read = async (response: Response) => new Uint8Array(await response.arrayBuffer())

async function opened(length: number, chunkSize = 64, overrides: Partial<StreamDeps> = {}) {
  const file = await sealed(length, chunkSize)
  const registry = createRegistry()
  registry.open(file.stream)
  const d = deps({ object: file.stored, ...overrides })
  const get = (range: string | null) => handleStreamRequest(request(range), CLIENT, registry, d)
  return { ...file, registry, deps: d, get }
}

describe('answering a range', () => {
  it.each([
    ['inside one chunk', 'bytes=10-20', 10, 20],
    ['across the line between two chunks', 'bytes=60-70', 60, 70],
    ['exactly one chunk', 'bytes=64-127', 64, 127],
    ['from the first byte of a chunk to the first of the next', 'bytes=64-128', 64, 128],
    ['the first byte', 'bytes=0-0', 0, 0],
    ['the last byte of the file', 'bytes=299-299', 299, 299],
    ['into the short last chunk', 'bytes=250-299', 250, 299],
    ['across several chunks', 'bytes=30-260', 30, 260],
  ])('gives the bytes of the file for a range %s', async (_, header, start, end) => {
    const file = await opened(300)

    const response = await file.get(header)

    expect(response.status).toBe(206)
    expect(Array.from(await read(response))).toEqual(Array.from(file.plain.subarray(start, end + 1)))
  })

  it('says which bytes it holds, how many there are, what they are, and that they are not to be kept', async () => {
    const file = await opened(300)

    const response = await file.get('bytes=100-199')

    expect(response.headers.get('content-range')).toBe('bytes 100-199/300')
    expect(response.headers.get('content-length')).toBe('100')
    expect(response.headers.get('content-type')).toBe('video/mp4')
    expect(response.headers.get('accept-ranges')).toBe('bytes')
    expect(response.headers.get('cache-control')).toBe('no-store')
    expect(response.headers.get('x-content-type-options')).toBe('nosniff')
  })

  it('gives the end of the file for an open range and for a suffix, and the whole of a small file when there is no header', async () => {
    const file = await opened(300)

    expect(Array.from(await read(await file.get('bytes=250-')))).toEqual(Array.from(file.plain.subarray(250)))
    expect(Array.from(await read(await file.get('bytes=-10')))).toEqual(Array.from(file.plain.subarray(290)))
    expect(Array.from(await read(await file.get(null)))).toEqual(Array.from(file.plain))
  })

  it('does not open more than an answer holds, however much is asked for', async () => {
    const total = MAX_RESPONSE_BYTES + 200_000
    const file = await opened(total, 64 * 1024)

    const response = await file.get('bytes=0-')

    expect(response.status).toBe(206)
    expect(response.headers.get('content-range')).toBe(`bytes 0-${MAX_RESPONSE_BYTES - 1}/${total}`)
    expect((await read(response)).length).toBe(MAX_RESPONSE_BYTES)
    // It read the chunks that hold the answer and not the rest of the file: a header and 32 chunks.
    const fetched = file.deps.fetchRange.mock.calls.reduce((sum, [, from, to]) => sum + (to - from + 1), 0)
    expect(fetched).toBeLessThan(MAX_RESPONSE_BYTES + 64 * 1024)
  })

  it('reads the header of the file once, for any number of ranges', async () => {
    const file = await opened(300)

    await file.get('bytes=0-10')
    await file.get('bytes=100-110')
    await file.get('bytes=200-210')

    expect(file.deps.fetchRange.mock.calls.filter(([, from, to]) => from === 0 && to === 12)).toHaveLength(1)
  })

  it('asks the bucket only for the chunks that hold the range', async () => {
    const file = await opened(300)

    await file.get('bytes=130-140')

    // The header, and chunk 2 (bytes 128 to 191 of the file, stored from 13 + 2 * 80).
    expect(file.deps.fetchRange.mock.calls.map(([, from, to]) => [from, to])).toEqual([
      [0, 12],
      [173, 252],
    ])
  })
})

describe('a range that cannot be answered', () => {
  it.each(['bytes=300-', 'bytes=400-500', 'bytes=5-2', 'bytes=0-1,5-6', 'items=0-1', 'bytes=-0'])('is a 416 that says how large the file is: %s', async (header) => {
    const file = await opened(300)

    const response = await file.get(header)

    expect(response.status).toBe(416)
    expect(response.headers.get('content-range')).toBe('bytes */300')
    expect(file.deps.fetchRange).not.toHaveBeenCalled()
  })
})

describe('a stream the worker does not have', () => {
  it('asks the page that made the request, and plays the stream the page gives, and keeps it', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    const d = deps({ object: file.stored, askPageForStream: async () => file.stream })

    const first = await handleStreamRequest(request('bytes=0-9'), CLIENT, registry, d)
    const second = await handleStreamRequest(request('bytes=10-19'), CLIENT, registry, d)

    expect(Array.from(await read(first))).toEqual(Array.from(file.plain.subarray(0, 10)))
    expect(Array.from(await read(second))).toEqual(Array.from(file.plain.subarray(10, 20)))
    expect(d.askPageForStream).toHaveBeenCalledExactlyOnceWith(CLIENT, ID)
  })

  it('is a 404 when the page has let go of it, without a request to the bucket', async () => {
    const d = deps({ object: new Uint8Array() })

    const response = await handleStreamRequest(request('bytes=0-9'), CLIENT, createRegistry(), d)

    expect(response.status).toBe(404)
    expect(d.fetchRange).not.toHaveBeenCalled()
  })

  it('is a 404 when the page answers with another stream, or with one that is not usable', async () => {
    const file = await sealed(300)

    const other = await handleStreamRequest(request('bytes=0-9'), CLIENT, createRegistry(), deps({ object: file.stored, askPageForStream: async () => ({ ...file.stream, id: OTHER_ID }) }))
    const unusable = await handleStreamRequest(request('bytes=0-9'), CLIENT, createRegistry(), deps({ object: file.stored, askPageForStream: async () => ({ ...file.stream, contentType: 'text/html' }) }))

    expect(other.status).toBe(404)
    expect(unusable.status).toBe(404)
  })

  it('is a 404 for an address that is not a stream', async () => {
    const response = await handleStreamRequest(new Request('https://chat.test/file-stream/nope'), CLIENT, createRegistry(), deps({ object: new Uint8Array() }))

    expect(response.status).toBe(404)
  })
})

describe('a link that has expired', () => {
  it('asks the page for a new one, once, and goes on with it', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    registry.open(file.stream)
    const object = ranged(file.stored)
    const d = deps({
      object: file.stored,
      fetchRange: async (url, from, to) => (url.includes('sig=1') ? new Response(null, { status: 403 }) : object(url, from, to)),
      askPageForLink: async () => 'https://bucket.test/file?sig=2',
    })

    const response = await handleStreamRequest(request('bytes=0-9'), CLIENT, registry, d)
    const next = await handleStreamRequest(request('bytes=100-109'), CLIENT, registry, d)

    expect(Array.from(await read(response))).toEqual(Array.from(file.plain.subarray(0, 10)))
    expect(Array.from(await read(next))).toEqual(Array.from(file.plain.subarray(100, 110)))
    // The new link is kept: only the first request met the old one.
    expect(d.askPageForLink).toHaveBeenCalledExactlyOnceWith(CLIENT, ID)
  })

  it('is a 502 when the new link is refused as well, and asks for no third', async () => {
    const file = await opened(300, 64, { fetchRange: async () => new Response(null, { status: 403 }), askPageForLink: async () => 'https://bucket.test/file?sig=2' })

    const response = await file.get('bytes=0-9')

    expect(response.status).toBe(502)
    expect(file.deps.askPageForLink).toHaveBeenCalledOnce()
  })

  it.each([null, 'javascript:alert(1)'])('is a 502 when the page has no new link (%j)', async (link) => {
    const file = await opened(300, 64, { fetchRange: async () => new Response(null, { status: 403 }), askPageForLink: async () => link })

    expect((await file.get('bytes=0-9')).status).toBe(502)
  })
})

describe('a file that cannot be trusted', () => {
  it('is a 502 when the bucket answers the whole object and not the range', async () => {
    const file = await opened(300, 64, { fetchRange: async () => new Response(new Uint8Array(10), { status: 200 }) })

    expect((await file.get('bytes=0-9')).status).toBe(502)
  })

  it('does not read the body of a bucket that answers the whole object and not the range, which may be a file of 100 MiB', async () => {
    const arrayBuffer = vi.fn(async () => new ArrayBuffer(10))
    const file = await opened(300, 64, { fetchRange: async () => ({ status: 200, arrayBuffer }) as unknown as Response })

    expect((await file.get('bytes=0-9')).status).toBe(502)
    expect(arrayBuffer).not.toHaveBeenCalled()
  })

  it('is a 502 when the bucket answers more bytes than were asked for, or fewer', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    registry.open(file.stream)
    const longer = deps({ object: file.stored, fetchRange: async (_url, from, to) => new Response(file.stored.slice(from, to + 1 + 16), { status: 206 }) })
    const shorter = deps({ object: file.stored, fetchRange: async (_url, from, to) => new Response(file.stored.slice(from, to), { status: 206 }) })

    expect((await handleStreamRequest(request('bytes=0-9'), CLIENT, registry, longer)).status).toBe(502)
    expect((await handleStreamRequest(request('bytes=0-9'), CLIENT, registry, shorter)).status).toBe(502)
  })

  it('is a 502 when the bucket cannot be reached', async () => {
    const file = await opened(300, 64, {
      fetchRange: async () => {
        throw new TypeError('network')
      },
    })

    expect((await file.get('bytes=0-9')).status).toBe(502)
  })

  it('is a 502, and gives no bytes, when one byte of the chunk has been changed', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    registry.open(file.stream)
    const damaged = new Uint8Array(file.stored)
    damaged[13 + 80 + 5] ^= 1
    const d = deps({ object: damaged })

    const bad = await handleStreamRequest(request('bytes=64-70'), CLIENT, registry, d)
    const good = await handleStreamRequest(request('bytes=0-10'), CLIENT, registry, d)

    expect(bad.status).toBe(502)
    expect(bad.headers.get('content-length')).toBeNull()
    expect(await read(bad)).toHaveLength(0)
    // A range that does not touch the changed chunk is still opened.
    expect(good.status).toBe(206)
  })

  it('is a 502 for a range that crosses into a chunk that has been changed, whole: nothing of the good chunk goes out either', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    registry.open(file.stream)
    const damaged = new Uint8Array(file.stored)
    damaged[13 + 80 + 5] ^= 1

    const response = await handleStreamRequest(request('bytes=60-70'), CLIENT, registry, deps({ object: damaged }))

    expect(response.status).toBe(502)
    expect(await read(response)).toHaveLength(0)
  })

  it('is a 502 when the file has been cut short, for the range that reaches the cut', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    registry.open(file.stream)
    const d = deps({ object: file.stored.subarray(0, file.stored.length - 30) })

    const early = await handleStreamRequest(request('bytes=0-10'), CLIENT, registry, d)
    const late = await handleStreamRequest(request('bytes=290-299'), CLIENT, registry, d)

    expect(early.status).toBe(206)
    expect(late.status).toBe(502)
  })

  it('is a 502 when the last chunk is not the last of a file of that size: a file that is longer than it was said to be', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    // Said to be 200 bytes, which is what four chunks hold: the real fifth chunk is then in a place the worker does not expect, and the
    // real last chunk is not sealed as the last of a file of 200 bytes.
    registry.open({ ...file.stream, size: 200, storedSize: encryptedFileLength(200, 64) })

    const response = await handleStreamRequest(request('bytes=190-199'), CLIENT, registry, deps({ object: file.stored }))

    expect(response.status).toBe(502)
  })

  it('is a 502 when the file is not cut the way the page said', async () => {
    const file = await sealed(300, 64)
    const registry = createRegistry()
    registry.open({ ...file.stream, chunkSize: 128, storedSize: encryptedFileLength(300, 128) })

    expect((await handleStreamRequest(request('bytes=0-9'), CLIENT, registry, deps({ object: file.stored }))).status).toBe(502)
  })

  it('is a 502 under another key', async () => {
    const file = await sealed(300)
    const registry = createRegistry()
    registry.open({ ...file.stream, key: newFileKey() })

    expect((await handleStreamRequest(request('bytes=0-9'), CLIENT, registry, deps({ object: file.stored }))).status).toBe(502)
  })
})

describe('the streams the worker keeps', () => {
  async function descriptor(id: string): Promise<StreamDescriptor> {
    return { ...(await sealed(300)).stream, id }
  }
  const ids = Array.from({ length: MAX_STREAMS + 1 }, (_, i) => `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`)

  it('keeps a stream that is usable and forgets it when it is closed', async () => {
    const registry = createRegistry()

    expect(registry.open(await descriptor(ID))).toBe(true)
    expect(registry.get(ID)?.stream.id).toBe(ID)

    registry.close(ID)

    expect(registry.get(ID)).toBeUndefined()
  })

  it.each([
    ['a key of the wrong length', { key: new Uint8Array(31) }],
    ['a type that is not a video', { contentType: 'text/html' }],
    ['a type that says it is a video and is another', { contentType: 'video/x-evil' }],
    ['sizes that do not agree', { storedSize: 12345 }],
    ['an empty file', { size: 0, storedSize: encryptedFileLength(0, 64) }],
    ['a chunk of nothing', { chunkSize: 0 }],
    ['a chunk too large', { chunkSize: 1 << 30 }],
    ['a link that is not to the web', { url: 'javascript:alert(1)' }],
    ['a link to a file', { url: 'file:///etc/passwd' }],
    ['an id that is not an id', { id: '../../x' }],
  ])('does not keep a stream with %s', async (_, change) => {
    const registry = createRegistry()
    const stream = { ...(await descriptor(ID)), ...change }

    expect(registry.open(stream)).toBe(false)
    expect(registry.get(stream.id)).toBeUndefined()
  })

  it('does not keep the key it was given: a change to that array later does not change the key of the stream', async () => {
    const registry = createRegistry()
    const stream = await descriptor(ID)
    const before = Array.from(stream.key)

    registry.open(stream)
    stream.key.fill(0)

    expect(Array.from(registry.get(ID)!.stream.key)).toEqual(before)
  })

  it('lets go of the oldest stream when there are more than it keeps', async () => {
    const registry = createRegistry()
    for (const id of ids) registry.open(await descriptor(id))

    expect(registry.get(ids[0])).toBeUndefined()
    expect(registry.get(ids[1])).toBeDefined()
    expect(registry.get(ids[MAX_STREAMS])).toBeDefined()
  })

  it('counts the last use and not the opening: the one that was played last is not the one let go of', async () => {
    const registry = createRegistry()
    for (const id of ids.slice(0, MAX_STREAMS)) registry.open(await descriptor(id))
    registry.get(ids[0])

    registry.open(await descriptor(ids[MAX_STREAMS]))

    expect(registry.get(ids[0])).toBeDefined()
    expect(registry.get(ids[1])).toBeUndefined()
  })

  it('has a stream opened again as it was just now: with its header read anew', async () => {
    const registry = createRegistry()
    registry.open(await descriptor(ID))
    registry.get(ID)!.header = { chunkSize: 64, noncePrefix: new Uint8Array(7) }

    registry.open(await descriptor(ID))

    expect(registry.get(ID)!.header).toBeNull()
  })
})
