import { type Mock, describe, expect, it, vi } from 'vitest'
import type { FileRef } from '../../../lib/crypto/message'
import { FileInvalidError, FileUnavailableError, type FileObjectDeps, type FileSource, createFileObjects } from './fileObjects'

const entry = (type = 'image/png'): FileRef => ({ name: 'a', type, size: 3, key: 'k'.repeat(43), chunkSize: 65536 })

const source = (id: string, type = 'image/png'): FileSource => ({
  id,
  conversationId: 'c1',
  messageId: 'm1',
  url: `https://files.test/${id}?old`,
  entry: entry(type),
})

const STORED = new Uint8Array([9, 9, 9])
const PLAIN = new Uint8Array([1, 2, 3])

type Spied = { [K in keyof FileObjectDeps]: Mock<FileObjectDeps[K]> }

// What stands in for the network, the cryptography and the browser: each is a spy, so a test says what went through it.
function deps(overrides: Partial<Spied> = {}): Spied {
  let made = 0
  return {
    fetchStored: vi.fn<FileObjectDeps['fetchStored']>(async () => STORED),
    renewLink: vi.fn<FileObjectDeps['renewLink']>(async (file) => `https://files.test/${file.id}?new`),
    open: vi.fn<FileObjectDeps['open']>(async () => PLAIN),
    makeUrl: vi.fn<FileObjectDeps['makeUrl']>(() => `blob:${++made}`),
    revokeUrl: vi.fn<FileObjectDeps['revokeUrl']>(),
    ...overrides,
  }
}

describe('showing a file', () => {
  it('fetches the encrypted file, opens it with the entry of the sealed message and hands back an address for the result', async () => {
    const d = deps()
    const objects = createFileObjects(d)
    const file = source('a1')

    const url = await objects.hold(file)

    expect(d.fetchStored).toHaveBeenCalledWith('https://files.test/a1?old')
    expect(d.open).toHaveBeenCalledWith(file.entry, STORED)
    expect(d.makeUrl).toHaveBeenCalledWith(PLAIN, 'image/png')
    expect(url).toBe('blob:1')
  })

  it('gives the bytes the type of what is drawn, not what the sender wrote in a different case or with parameters', async () => {
    const d = deps()
    const objects = createFileObjects(d)

    await objects.hold(source('a1', 'Image/PNG; charset=x'))

    expect(d.makeUrl).toHaveBeenCalledWith(PLAIN, 'image/png')
  })

  it('plays a video as a video', async () => {
    const d = deps()

    await createFileObjects(d).hold(source('v1', 'video/mp4'))

    expect(d.makeUrl).toHaveBeenCalledWith(PLAIN, 'video/mp4')
  })

  it.each(['image/svg+xml', 'text/html', 'application/pdf', 'application/octet-stream', 'image/x-icon', ''])(
    'draws nothing for a file that says it is %s, and does not even fetch it',
    async (type) => {
      const d = deps()

      expect(await createFileObjects(d).hold(source('a1', type))).toBeNull()

      expect(d.fetchStored).not.toHaveBeenCalled()
      expect(d.makeUrl).not.toHaveBeenCalled()
    },
  )

  it('opens a file once however many places show it', async () => {
    const d = deps()
    const objects = createFileObjects(d)

    const [one, two] = await Promise.all([objects.hold(source('a1')), objects.hold(source('a1'))])
    const three = await objects.hold(source('a1'))

    expect(one).toBe(two)
    expect(three).toBe(one)
    expect(d.fetchStored).toHaveBeenCalledTimes(1)
    expect(d.makeUrl).toHaveBeenCalledTimes(1)
  })

  it('opens each file by itself', async () => {
    const d = deps()
    const objects = createFileObjects(d)

    const urls = await Promise.all([objects.hold(source('a1')), objects.hold(source('a2'))])

    expect(urls).toEqual(['blob:1', 'blob:2'])
  })
})

describe('a link that has expired', () => {
  it('asks for a new link and fetches again with it, once', async () => {
    const d = deps({ fetchStored: vi.fn<FileObjectDeps['fetchStored']>().mockRejectedValueOnce(new Error('403')).mockResolvedValue(STORED) })

    const url = await createFileObjects(d).hold(source('a1'))

    expect(d.renewLink).toHaveBeenCalledTimes(1)
    expect(d.fetchStored.mock.calls.map(([address]) => address)).toEqual(['https://files.test/a1?old', 'https://files.test/a1?new'])
    expect(url).toBe('blob:1')
  })

  it('does not ask for a new link when the one held works', async () => {
    const d = deps()

    await createFileObjects(d).hold(source('a1'))

    expect(d.renewLink).not.toHaveBeenCalled()
  })

  it('gives up with a failure of its own when the new link does not work either, without asking a third time', async () => {
    const d = deps({ fetchStored: vi.fn<FileObjectDeps['fetchStored']>().mockRejectedValue(new Error('403')) })

    await expect(createFileObjects(d).hold(source('a1'))).rejects.toBeInstanceOf(FileUnavailableError)

    expect(d.fetchStored).toHaveBeenCalledTimes(2)
    expect(d.renewLink).toHaveBeenCalledTimes(1)
  })

  it('gives up with the same failure when no new link can be had', async () => {
    const d = deps({
      fetchStored: vi.fn<FileObjectDeps['fetchStored']>().mockRejectedValue(new Error('403')),
      renewLink: vi.fn<FileObjectDeps['renewLink']>().mockRejectedValue(new Error('404')),
    })

    await expect(createFileObjects(d).hold(source('a1'))).rejects.toBeInstanceOf(FileUnavailableError)

    expect(d.fetchStored).toHaveBeenCalledTimes(1)
  })

  it('does not say what went wrong in the words of the storage or the network', async () => {
    const d = deps({ fetchStored: vi.fn<FileObjectDeps['fetchStored']>().mockRejectedValue(new Error('The storage answered 403.')) })

    const failure = await createFileObjects(d).hold(source('a1')).catch((error: unknown) => error)

    expect((failure as Error).message).not.toContain('403')
    expect((failure as Error).message).toBe('The file could not be fetched.')
  })
})

describe('a file that is not the one the message describes', () => {
  it('is refused with a failure of its own, and nothing is drawn', async () => {
    const d = deps({ open: vi.fn<FileObjectDeps['open']>().mockRejectedValue(new Error('The tag does not match')) })

    await expect(createFileObjects(d).hold(source('a1'))).rejects.toBeInstanceOf(FileInvalidError)

    expect(d.makeUrl).not.toHaveBeenCalled()
  })

  it('is not mended by a new link, which would fetch the same file', async () => {
    const d = deps({ open: vi.fn<FileObjectDeps['open']>().mockRejectedValue(new Error('The tag does not match')) })

    await createFileObjects(d).hold(source('a1')).catch(() => undefined)

    expect(d.renewLink).not.toHaveBeenCalled()
    expect(d.fetchStored).toHaveBeenCalledTimes(1)
  })
})

describe('a failure', () => {
  it('is not kept: the next to ask tries again', async () => {
    const d = deps({ fetchStored: vi.fn<FileObjectDeps['fetchStored']>().mockRejectedValueOnce(new Error('x')).mockRejectedValueOnce(new Error('y')).mockResolvedValue(STORED) })
    const objects = createFileObjects(d)

    await expect(objects.hold(source('a1'))).rejects.toBeInstanceOf(FileUnavailableError)
    const url = await objects.hold(source('a1'))

    expect(url).toBe('blob:1')
  })

  it('reaches everyone who asked while the file was being fetched', async () => {
    const d = deps({ open: vi.fn<FileObjectDeps['open']>().mockRejectedValue(new Error('x')) })
    const objects = createFileObjects(d)

    const results = await Promise.allSettled([objects.hold(source('a1')), objects.hold(source('a1'))])

    expect(results.map((r) => r.status)).toEqual(['rejected', 'rejected'])
    expect(d.fetchStored).toHaveBeenCalledTimes(1)
  })
})

describe('letting go of files', () => {
  async function holdAndRelease(objects: ReturnType<typeof createFileObjects>, count: number) {
    for (let i = 0; i < count; i++) {
      const file = source(`f${i}`)
      await objects.hold(file)
      objects.release(file)
    }
  }

  it('keeps a file that nobody is showing, to show it again without fetching', async () => {
    const d = deps()
    const objects = createFileObjects(d)
    const file = source('a1')

    await objects.hold(file)
    objects.release(file)
    await objects.hold(file)

    expect(d.fetchStored).toHaveBeenCalledTimes(1)
    expect(d.revokeUrl).not.toHaveBeenCalled()
  })

  it('lets go of the oldest files nobody is showing once there are more than are kept, and revokes their addresses', async () => {
    const d = deps()
    const objects = createFileObjects(d, (() => {
      let tick = 0
      return () => ++tick
    })())

    await holdAndRelease(objects, 26)
    await Promise.resolve()

    expect(d.revokeUrl).toHaveBeenCalledTimes(2)
    expect(d.revokeUrl).toHaveBeenCalledWith('blob:1')
    expect(d.revokeUrl).toHaveBeenCalledWith('blob:2')
  })

  it('opens a file that was let go of again, from the start', async () => {
    const d = deps()
    const objects = createFileObjects(d, (() => {
      let tick = 0
      return () => ++tick
    })())

    await holdAndRelease(objects, 26)
    await objects.hold(source('f0'))

    expect(d.fetchStored).toHaveBeenCalledTimes(27)
  })

  it('never lets go of a file that is being shown, however many are kept besides', async () => {
    const d = deps()
    const objects = createFileObjects(d, (() => {
      let tick = 0
      return () => ++tick
    })())
    const shown = source('shown')
    await objects.hold(shown)

    await holdAndRelease(objects, 40)
    await Promise.resolve()

    expect(d.revokeUrl).not.toHaveBeenCalledWith('blob:1')
    await objects.hold(shown)
    expect(d.fetchStored).toHaveBeenCalledTimes(41)
  })

  it('keeps a file that two places show until both have let go of it', async () => {
    const d = deps()
    const objects = createFileObjects(d, (() => {
      let tick = 0
      return () => ++tick
    })())
    const file = source('shared')
    await objects.hold(file)
    await objects.hold(file)

    objects.release(file)
    await holdAndRelease(objects, 40)
    await Promise.resolve()

    expect(d.revokeUrl).not.toHaveBeenCalledWith('blob:1')
  })

  it('ignores a release of a file it does not hold', () => {
    const objects = createFileObjects(deps())

    expect(() => objects.release(source('nothing'))).not.toThrow()
  })

  it('revokes every address it made when it is let go of altogether', async () => {
    const d = deps()
    const objects = createFileObjects(d)
    await objects.hold(source('a1'))
    await objects.hold(source('a2'))

    objects.dispose()
    await Promise.resolve()

    expect(d.revokeUrl.mock.calls.map(([url]) => url).sort()).toEqual(['blob:1', 'blob:2'])
  })
})

describe('reading a file to save it', () => {
  it('gives the bytes of any type of file, fetching and opening it', async () => {
    const d = deps()

    const bytes = await createFileObjects(d).read(source('a1', 'application/pdf'))

    expect(bytes).toBe(PLAIN)
    expect(d.open).toHaveBeenCalledWith(expect.objectContaining({ type: 'application/pdf' }), STORED)
  })

  it('makes no address, and keeps nothing', async () => {
    const d = deps()
    const objects = createFileObjects(d)

    await objects.read(source('a1', 'application/pdf'))
    await objects.read(source('a1', 'application/pdf'))

    expect(d.makeUrl).not.toHaveBeenCalled()
    expect(d.fetchStored).toHaveBeenCalledTimes(2)
  })

  it('renews an expired link once, like showing does', async () => {
    const d = deps({ fetchStored: vi.fn<FileObjectDeps['fetchStored']>().mockRejectedValueOnce(new Error('403')).mockResolvedValue(STORED) })

    await createFileObjects(d).read(source('a1', 'application/pdf'))

    expect(d.renewLink).toHaveBeenCalledTimes(1)
  })

  it('fails with a failure of its own for a file that is not the one described', async () => {
    const d = deps({ open: vi.fn<FileObjectDeps['open']>().mockRejectedValue(new Error('x')) })

    await expect(createFileObjects(d).read(source('a1', 'application/pdf'))).rejects.toBeInstanceOf(FileInvalidError)
  })
})
