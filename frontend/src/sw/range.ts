import { FILE_HEADER_BYTES, encryptedFileLength } from '../lib/crypto/file'
import { TAG_BYTES } from '../lib/crypto/formats'
import type { StreamDescriptor } from './protocol'

// The maths of answering a range request from an encrypted file: which bytes of the file a request asks for, how much of it one answer
// holds, and which chunks of the stored file cover it. Pure, so it is tested without a worker.

// The most one answer holds. A player asks for "from here to the end", and the answer is a part of that; it asks again for the next part.
// Without this, a request for a whole video would decrypt the whole video.
export const MAX_RESPONSE_BYTES = 2 * 1024 * 1024

// Bytes of a file from `start` to `end`, both included.
export interface ByteRange {
  start: number
  end: number
}

const RANGE = /^bytes=(\d*)-(\d*)$/

// The range a Range header asks for in a file of `size` bytes. A header that is not there asks for the whole file. Anything that cannot be
// answered is 'unsatisfiable' (a 416): another unit, several ranges, a start past the end, an end before the start, a suffix of nothing.
export function parseRange(header: string | null, size: number): ByteRange | 'unsatisfiable' {
  if (size <= 0) return 'unsatisfiable'
  if (header === null || header.trim() === '') return { start: 0, end: size - 1 }

  const match = RANGE.exec(header.trim())
  if (!match) return 'unsatisfiable'

  const [, from, to] = match
  if (from === '' && to === '') return 'unsatisfiable'

  // The last n bytes.
  if (from === '') {
    const last = Number(to)
    return last === 0 ? 'unsatisfiable' : { start: Math.max(0, size - last), end: size - 1 }
  }

  const start = Number(from)
  if (start >= size) return 'unsatisfiable'
  if (to === '') return { start, end: size - 1 }

  const end = Number(to)
  return end < start ? 'unsatisfiable' : { start, end: Math.min(end, size - 1) }
}

export function capRange(range: ByteRange, most = MAX_RESPONSE_BYTES): ByteRange {
  return { start: range.start, end: Math.min(range.end, range.start + most - 1) }
}

// The chunks that hold a range and where they are in the stored file.
export interface ChunkSpan {
  // The first and the last chunk the range touches, counting from 0.
  first: number
  last: number
  // The index of the last chunk of the whole file, which is sealed as the last.
  lastIndex: number
  // Where those chunks are in the stored file, both included.
  storedFrom: number
  storedTo: number
  // How many bytes into the opened chunks the range starts.
  skip: number
}

export function chunkSpan(range: ByteRange, chunkSize: number, size: number): ChunkSpan {
  const stride = chunkSize + TAG_BYTES
  const first = Math.floor(range.start / chunkSize)
  const last = Math.floor(range.end / chunkSize)

  return {
    first,
    last,
    lastIndex: Math.max(1, Math.ceil(size / chunkSize)) - 1,
    storedFrom: FILE_HEADER_BYTES + first * stride,
    // The last chunk of the file is shorter than the others.
    storedTo: Math.min(FILE_HEADER_BYTES + (last + 1) * stride, encryptedFileLength(size, chunkSize)) - 1,
    skip: range.start - first * chunkSize,
  }
}

// The headers of an answer. It is never kept (what it holds is the opened file) and never taken for another type.
export function answerHeaders(range: ByteRange, stream: Pick<StreamDescriptor, 'size' | 'contentType'>): Record<string, string> {
  return {
    'Content-Type': stream.contentType,
    'Content-Range': `bytes ${range.start}-${range.end}/${stream.size}`,
    'Content-Length': String(range.end - range.start + 1),
    'Accept-Ranges': 'bytes',
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
  }
}

export function unsatisfiableHeaders(size: number): Record<string, string> {
  return { 'Content-Range': `bytes */${size}`, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }
}
