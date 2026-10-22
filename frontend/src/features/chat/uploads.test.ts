import { describe, expect, it } from 'vitest'
import { MAX_FILES_PER_MESSAGE } from './attachments'
import {
  type UploadItem,
  addFiles,
  canSend,
  markFailed,
  markReady,
  removeItem,
  setProgress,
  toPendingAttachments,
} from './uploads'

const fake = (name: string, type = 'image/png', size = 1000) => ({ name, type, size }) as File

let counter = 0
const newId = () => `u${++counter}`

function listOf(...files: File[]): UploadItem[] {
  return addFiles([], files, newId).items
}

describe('addFiles', () => {
  it('adds each accepted file as an upload that has just started', () => {
    const { items, added, rejected } = addFiles([], [fake('a.png'), fake('b.pdf', 'application/pdf')], newId)

    expect(items).toHaveLength(2)
    expect(added).toEqual(items)
    expect(rejected).toEqual([])
    expect(items[0]).toMatchObject({ status: 'uploading', progress: 0, uploadKey: null, error: null })
    expect(items[0].id).not.toBe(items[1].id)
  })

  it('refuses a file that may not be attached and says why, while keeping the others', () => {
    const { items, rejected } = addFiles([], [fake('a.png'), fake('setup.exe', 'application/x-msdownload')], newId)

    expect(items.map((i) => i.file.name)).toEqual(['a.png'])
    expect(rejected).toHaveLength(1)
    expect(rejected[0]).toContain('setup.exe')
  })

  it('counts the files already in the list towards the limit', () => {
    const first = listOf(...Array.from({ length: MAX_FILES_PER_MESSAGE - 1 }, (_, i) => fake(`${i}.png`)))

    const { items, rejected } = addFiles(first, [fake('fits.png'), fake('too-many.png')], newId)

    expect(items).toHaveLength(MAX_FILES_PER_MESSAGE)
    expect(rejected).toHaveLength(1)
    expect(rejected[0]).toContain(String(MAX_FILES_PER_MESSAGE))
  })

  it('counts a failed upload too, because it is still in the list until it is removed', () => {
    const full = listOf(...Array.from({ length: MAX_FILES_PER_MESSAGE }, (_, i) => fake(`${i}.png`)))
    const withFailure = markFailed(full, full[0].id, 'No')

    expect(addFiles(withFailure, [fake('more.png')], newId).rejected).toHaveLength(1)
  })

  it('returns the same list when nothing was added', () => {
    const items = listOf(fake('a.png'))

    expect(addFiles(items, [fake('x.exe', 'application/x-msdownload')], newId).items).toBe(items)
    expect(addFiles(items, [], newId).items).toBe(items)
  })
})

describe('upload progress and outcome', () => {
  it('records progress', () => {
    const items = listOf(fake('a.png'))

    expect(setProgress(items, items[0].id, 0.4)[0].progress).toBe(0.4)
  })

  it('marks an upload ready with the key the server gave, and complete', () => {
    const items = listOf(fake('a.png'))

    const ready = markReady(items, items[0].id, 'pending/c/u/k')[0]

    expect(ready).toMatchObject({ status: 'ready', progress: 1, uploadKey: 'pending/c/u/k' })
  })

  it('marks an upload failed with the reason', () => {
    const items = listOf(fake('a.png'))

    expect(markFailed(items, items[0].id, 'No connection')[0]).toMatchObject({ status: 'failed', error: 'No connection' })
  })

  it('ignores progress, success and failure after an upload has ended', () => {
    const start = listOf(fake('a.png'))
    const id = start[0].id
    const ready = markReady(start, id, 'k1')
    const failed = markFailed(start, id, 'boom')

    expect(setProgress(ready, id, 0.2)).toBe(ready)
    expect(markFailed(ready, id, 'late failure')).toBe(ready)
    expect(markReady(ready, id, 'k2')).toBe(ready)
    expect(setProgress(failed, id, 0.9)).toBe(failed)
    expect(markReady(failed, id, 'k3')).toBe(failed)
  })

  it('returns the same list for an id that is not there', () => {
    const items = listOf(fake('a.png'))

    expect(setProgress(items, 'nope', 0.5)).toBe(items)
    expect(markReady(items, 'nope', 'k')).toBe(items)
    expect(markFailed(items, 'nope', 'x')).toBe(items)
    expect(removeItem(items, 'nope')).toBe(items)
  })

  it('returns the same list when the progress did not change', () => {
    const items = listOf(fake('a.png'))

    expect(setProgress(items, items[0].id, 0)).toBe(items)
  })

  it('changes only the upload it is about', () => {
    const items = listOf(fake('a.png'), fake('b.png'))

    const next = markReady(items, items[1].id, 'k')

    expect(next[0]).toBe(items[0])
    expect(next[1].status).toBe('ready')
  })

  it('removes an upload', () => {
    const items = listOf(fake('a.png'), fake('b.png'))

    expect(removeItem(items, items[0].id).map((i) => i.file.name)).toEqual(['b.png'])
  })
})

describe('canSend', () => {
  it('needs something to say or a file to send', () => {
    expect(canSend([], '')).toBe(false)
    expect(canSend([], '   ')).toBe(false)
    expect(canSend([], 'hello')).toBe(true)
  })

  it('allows a message of only files once they are all uploaded', () => {
    const items = listOf(fake('a.png'))

    expect(canSend(items, '')).toBe(false)
    expect(canSend(markReady(items, items[0].id, 'k'), '')).toBe(true)
  })

  it('waits while any upload is still going, even with text', () => {
    const items = listOf(fake('a.png'), fake('b.png'))
    const oneReady = markReady(items, items[0].id, 'k')

    expect(canSend(oneReady, 'look at these')).toBe(false)
  })

  it('does not send past a failed upload, which has to be removed first', () => {
    const items = listOf(fake('a.png'), fake('b.png'))
    const mixed = markFailed(markReady(items, items[0].id, 'k'), items[1].id, 'No')

    expect(canSend(mixed, 'text')).toBe(false)
    expect(canSend(removeItem(mixed, items[1].id), 'text')).toBe(true)
  })
})

describe('toPendingAttachments', () => {
  it('describes the files that are ready and leaves out the others', () => {
    const items = listOf(fake('a.png', 'image/png', 500), fake('b.pdf', 'application/pdf', 900), fake('c.mp4', 'video/mp4'))
    const mixed = markReady(markReady(items, items[0].id, 'k1'), items[1].id, 'k2')

    const pending = toPendingAttachments(mixed)

    expect(pending.map((p) => [p.uploadKey, p.fileName, p.type, p.sizeBytes])).toEqual([
      ['k1', 'a.png', 'Image', 500],
      ['k2', 'b.pdf', 'File', 900],
    ])
  })
})
