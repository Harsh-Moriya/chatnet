import { describe, expect, it } from 'vitest'
import { encryptedFileLength } from '../../../lib/crypto/file'
import type { FileRef } from '../../../lib/crypto/message'
import type { Attachment, AttachmentWire } from '../types'
import { entriesOf, joinFiles, unvouched } from './joinFiles'

const entry = (name: string, size: number, chunkSize = 65536): FileRef => ({ name, type: 'image/png', size, key: 'a'.repeat(43), chunkSize })

// The attachment the server would hold for a file of this size.
const stored = (id: string, size: number, chunkSize = 65536): AttachmentWire => ({
  id,
  sizeBytes: encryptedFileLength(size, chunkSize),
  url: `https://files.test/${id}`,
})

describe('pairing the files of a message with the sealed entries', () => {
  it('pairs the n-th attachment with the n-th entry', () => {
    const entries = [entry('one.png', 100), entry('two.png', 200), entry('three.png', 300)]

    const joined = joinFiles([stored('a', 100), stored('b', 200), stored('c', 300)], entries)

    expect(joined.map((a) => [a.id, a.file?.name])).toEqual([['a', 'one.png'], ['b', 'two.png'], ['c', 'three.png']])
  })

  it('keeps the id, the stored size and the link of each attachment', () => {
    const [joined] = joinFiles([stored('a', 100)], [entry('one.png', 100)])

    expect(joined).toMatchObject({ id: 'a', sizeBytes: encryptedFileLength(100, 65536), url: 'https://files.test/a' })
  })

  it('keeps the entry as it was sealed, so that an edit seals it again unchanged', () => {
    const sealed = entry('../x‮gpj.exe', 100)

    const [joined] = joinFiles([stored('a', 100)], [sealed])

    expect(joined.file).toBe(sealed)
  })

  it('does not vouch for an attachment whose entry is for a file of another size', () => {
    const [joined] = joinFiles([stored('a', 100)], [entry('one.png', 101)])

    expect(joined.file).toBeNull()
  })

  it('does not vouch for an attachment whose entry says another chunk size when the sizes would differ', () => {
    // 200 bytes in chunks of 64 are four chunks, and in chunks of 128 two.
    const [joined] = joinFiles([stored('a', 200, 64)], [entry('one.png', 200, 128)])

    expect(joined.file).toBeNull()
  })

  it('does not vouch for an attachment the sealed message has no entry for', () => {
    const joined = joinFiles([stored('a', 100), stored('b', 200)], [entry('one.png', 100)])

    expect(joined.map((a) => a.file?.name ?? null)).toEqual(['one.png', null])
  })

  it('does not vouch for any attachment of a message that lists no files, such as one sealed before files were sealed', () => {
    const joined = joinFiles([stored('a', 100)], [])

    expect(joined.map((a) => a.file)).toEqual([null])
  })

  it('ignores an entry that has no attachment', () => {
    expect(joinFiles([], [entry('one.png', 100)])).toEqual([])
  })

  it('does not move an entry to another attachment when the attachments come in another order', () => {
    const joined = joinFiles([stored('b', 200), stored('a', 100)], [entry('one.png', 100), entry('two.png', 200)])

    expect(joined.map((a) => a.file)).toEqual([null, null])
  })
})

describe('an attachment of a message that could not be opened', () => {
  it('is not vouched for', () => {
    expect(unvouched([stored('a', 100)]).map((a) => a.file)).toEqual([null])
  })
})

describe('the entries to seal again with an edit', () => {
  const attached = (name: string, size: number): Attachment => ({ ...stored(name, size), file: entry(name, size) })

  it('are those of the attachments, in order', () => {
    expect(entriesOf([attached('one', 1), attached('two', 2)])?.map((e) => e.name)).toEqual(['one', 'two'])
  })

  it('are none for a message without files', () => {
    expect(entriesOf([])).toEqual([])
  })

  it('cannot be given when one attachment has none, which would shift the others out of place', () => {
    expect(entriesOf([attached('one', 1), { ...stored('b', 2), file: null }, attached('three', 3)])).toBeNull()
  })
})
