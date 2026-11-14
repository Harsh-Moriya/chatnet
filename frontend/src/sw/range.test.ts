import { describe, expect, it } from 'vitest'
import { FILE_HEADER_BYTES, decryptChunk, encryptFile, newFileKey, readFileHeader } from '../lib/crypto/file'
import { streamIdOf, streamUrl } from './protocol'
import { MAX_RESPONSE_BYTES, answerHeaders, capRange, chunkSpan, parseRange, unsatisfiableHeaders } from './range'

describe('what a Range header asks for', () => {
  it.each([
    ['bytes=0-99', { start: 0, end: 99 }],
    ['bytes=100-199', { start: 100, end: 199 }],
    ['bytes=500-', { start: 500, end: 999 }],
    ['bytes=900-5000', { start: 900, end: 999 }],
    ['bytes=-100', { start: 900, end: 999 }],
    ['bytes=-5000', { start: 0, end: 999 }],
    ['bytes=999-999', { start: 999, end: 999 }],
  ])('%s is %j of a file of 1000 bytes', (header, expected) => {
    expect(parseRange(header, 1000)).toEqual(expected)
  })

  it.each([null, '', '   '])('asks for the whole file when there is no header (%j)', (header) => {
    expect(parseRange(header, 1000)).toEqual({ start: 0, end: 999 })
  })

  it.each(['bytes=1000-', 'bytes=1000-1500', 'bytes=5-2', 'bytes=-0', 'bytes=-', 'bytes=0-1,5-6', 'items=0-1', 'bytes=a-b', 'bytes 0-1', '0-1'])(
    'cannot answer %j',
    (header) => {
      expect(parseRange(header, 1000)).toBe('unsatisfiable')
    },
  )

  it('cannot answer anything for a file with nothing in it', () => {
    expect(parseRange('bytes=0-', 0)).toBe('unsatisfiable')
    expect(parseRange(null, 0)).toBe('unsatisfiable')
  })

  it('reads a header with spaces around it', () => {
    expect(parseRange('  bytes=10-19 ', 1000)).toEqual({ start: 10, end: 19 })
  })
})

describe('how much one answer holds', () => {
  it('leaves a range that is small enough as it is', () => {
    expect(capRange({ start: 10, end: 5000 })).toEqual({ start: 10, end: 5000 })
  })

  it('cuts a range at the most an answer holds, from where it starts', () => {
    expect(capRange({ start: 100, end: 99_999_999 })).toEqual({ start: 100, end: 100 + MAX_RESPONSE_BYTES - 1 })
  })

  it('holds exactly the most when the range is exactly that long', () => {
    expect(capRange({ start: 0, end: MAX_RESPONSE_BYTES - 1 })).toEqual({ start: 0, end: MAX_RESPONSE_BYTES - 1 })
    expect(capRange({ start: 0, end: MAX_RESPONSE_BYTES })).toEqual({ start: 0, end: MAX_RESPONSE_BYTES - 1 })
  })
})

describe('which chunks hold a range', () => {
  // A file of 300 bytes in chunks of 64: four full chunks and one of 44. A chunk is 80 bytes stored.
  const size = 300
  const chunkSize = 64

  it('is one chunk for a range inside it', () => {
    expect(chunkSpan({ start: 10, end: 20 }, chunkSize, size)).toEqual({ first: 0, last: 0, lastIndex: 4, storedFrom: 13, storedTo: 13 + 80 - 1, skip: 10 })
  })

  it('is two chunks for a range across the line between them, with the skip inside the first', () => {
    expect(chunkSpan({ start: 60, end: 70 }, chunkSize, size)).toEqual({ first: 0, last: 1, lastIndex: 4, storedFrom: 13, storedTo: 13 + 160 - 1, skip: 60 })
  })

  it('starts at the start of a chunk when the range does, and ends in it when the range ends on its last byte', () => {
    expect(chunkSpan({ start: 64, end: 127 }, chunkSize, size)).toEqual({ first: 1, last: 1, lastIndex: 4, storedFrom: 13 + 80, storedTo: 13 + 160 - 1, skip: 0 })
  })

  it('goes on into the next chunk for a range that ends on the first byte of it', () => {
    expect(chunkSpan({ start: 64, end: 128 }, chunkSize, size)).toMatchObject({ first: 1, last: 2 })
  })

  it('ends the stored range at the end of the stored file for the short last chunk', () => {
    const span = chunkSpan({ start: 290, end: 299 }, chunkSize, size)

    // 13 + 4 * 80 + 44 + 16 = 393 bytes stored.
    expect(span).toEqual({ first: 4, last: 4, lastIndex: 4, storedFrom: 13 + 320, storedTo: 392, skip: 34 })
  })

  it('has the last chunk of a file that is a whole number of chunks as the one before the end', () => {
    expect(chunkSpan({ start: 0, end: 0 }, 64, 128).lastIndex).toBe(1)
  })

  it('finds, in a file really sealed, exactly the bytes of the chunks it names, which open as the chunks of those numbers', async () => {
    const key = newFileKey()
    const plain = Uint8Array.from({ length: size }, (_, i) => (i * 11) % 251)
    const stored = await encryptFile(key, plain, { chunkSize })
    const header = readFileHeader(stored)

    for (const range of [{ start: 0, end: 0 }, { start: 60, end: 70 }, { start: 130, end: 250 }, { start: 290, end: 299 }, { start: 0, end: 299 }]) {
      const span = chunkSpan(range, chunkSize, size)
      const slice = stored.subarray(span.storedFrom, span.storedTo + 1)
      const parts: Uint8Array[] = []
      for (let index = span.first; index <= span.last; index++) {
        const from = (index - span.first) * (chunkSize + 16)
        parts.push(await decryptChunk(key, header, index, index === span.lastIndex, slice.subarray(from, from + chunkSize + 16)))
      }
      const opened = Uint8Array.from(parts.flatMap((part) => [...part])).subarray(span.skip, span.skip + (range.end - range.start + 1))

      expect(Array.from(opened)).toEqual(Array.from(plain.subarray(range.start, range.end + 1)))
    }
    expect(FILE_HEADER_BYTES).toBe(13)
  })
})

describe('what an answer says', () => {
  it('says which bytes of how many it holds, how long it is, what it is, and that it may be asked for in parts', () => {
    expect(answerHeaders({ start: 100, end: 199 }, { size: 5000, contentType: 'video/mp4' })).toMatchObject({
      'Content-Type': 'video/mp4',
      'Content-Range': 'bytes 100-199/5000',
      'Content-Length': '100',
      'Accept-Ranges': 'bytes',
    })
  })

  it('is not kept and is not taken for another type', () => {
    expect(answerHeaders({ start: 0, end: 0 }, { size: 1, contentType: 'video/webm' })).toMatchObject({
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
    })
    expect(unsatisfiableHeaders(300)).toMatchObject({ 'Content-Range': 'bytes */300', 'Cache-Control': 'no-store' })
  })
})

describe('the address of a stream', () => {
  const id = '7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'

  it('is the stream path and the id, and reads back as that id', () => {
    expect(streamUrl(id)).toBe(`/file-stream/${id}`)
    expect(streamIdOf(`/file-stream/${id}`)).toBe(id)
  })

  it.each(['/', '/file-stream/', '/file-stream/not-an-id', `/file-stream/${'7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'}/more`, '/chat/7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f', `/x/file-stream/${'7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'}`])(
    'is no stream: %s',
    (path) => {
      expect(streamIdOf(path)).toBeNull()
    },
  )
})
