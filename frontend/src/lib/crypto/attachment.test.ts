import { describe, expect, it } from 'vitest'
import { CryptoError, fromBase64Url, toBase64Url } from './bytes'
import { encryptedFileLength, newFileKey } from './file'
import { openAttachment, sealAttachment } from './attachment'

const bytesOf = (length: number) => Uint8Array.from({ length }, (_, i) => (i * 7 + 3) % 256)

describe('sealing a file for a message', () => {
  it('makes an entry that says what the file is, the size it has now and the key that opens it', async () => {
    const plain = bytesOf(300)

    const { ref } = await sealAttachment({ name: 'plan.pdf', type: 'application/pdf' }, plain)

    expect(ref).toMatchObject({ name: 'plan.pdf', type: 'application/pdf', size: 300, chunkSize: 65536 })
    expect(fromBase64Url(ref.key)).toHaveLength(32)
  })

  it('stores a file of the length the entry says it will have', async () => {
    const { stored, ref } = await sealAttachment({ name: 'a', type: 'text/plain' }, bytesOf(70_000))

    expect(stored).toHaveLength(encryptedFileLength(ref.size, ref.chunkSize))
  })

  it('gives every file a key of its own, and a different stored file every time', async () => {
    const plain = bytesOf(100)

    const first = await sealAttachment({ name: 'a', type: 'text/plain' }, plain)
    const second = await sealAttachment({ name: 'a', type: 'text/plain' }, plain)

    expect(first.ref.key).not.toBe(second.ref.key)
    expect(first.stored).not.toEqual(second.stored)
  })

  it('keeps the name and the type as they are given and leaves making them harmless to the app', async () => {
    const { ref } = await sealAttachment({ name: '../x‮gpj.exe', type: 'TEXT/HTML' }, bytesOf(1))

    expect(ref.name).toBe('../x‮gpj.exe')
    expect(ref.type).toBe('TEXT/HTML')
  })

  it('writes the chunk size it was asked for into the entry', async () => {
    const { ref, stored } = await sealAttachment({ name: 'a', type: 'text/plain' }, bytesOf(200), { chunkSize: 64 })

    expect(ref.chunkSize).toBe(64)
    expect(stored).toHaveLength(encryptedFileLength(200, 64))
  })
})

describe('opening a file with its entry', () => {
  it.each([0, 1, 63, 64, 65, 128, 129, 300])('gives back a file of %i bytes as it was', async (length) => {
    const plain = bytesOf(length)
    const { stored, ref } = await sealAttachment({ name: 'a', type: 'text/plain' }, plain, { chunkSize: 64 })

    expect(await openAttachment(ref, stored)).toEqual(plain)
  })

  it('opens a file of the usual chunk size across more than one chunk', async () => {
    const plain = bytesOf(70_000)
    const { stored, ref } = await sealAttachment({ name: 'a', type: 'application/zip' }, plain)

    expect(await openAttachment(ref, stored)).toEqual(plain)
  })

  describe('refuses', () => {
    const plain = bytesOf(200)

    async function sealed() {
      return sealAttachment({ name: 'a', type: 'text/plain' }, plain, { chunkSize: 64 })
    }

    it('a file with any one byte changed', async () => {
      const { stored, ref } = await sealed()

      for (const position of [0, 5, 13, 14, 70, 100, stored.length - 1]) {
        const damaged = new Uint8Array(stored)
        damaged[position] ^= 1

        await expect(openAttachment(ref, damaged)).rejects.toBeInstanceOf(CryptoError)
      }
    })

    it('a file that was cut off at the end', async () => {
      const { stored, ref } = await sealed()

      await expect(openAttachment(ref, stored.subarray(0, stored.length - 80))).rejects.toThrow('not the size')
    })

    it('a file with a chunk added at the end', async () => {
      const { stored, ref } = await sealed()
      const longer = new Uint8Array(stored.length + 80)
      longer.set(stored)

      await expect(openAttachment(ref, longer)).rejects.toThrow('not the size')
    })

    it('a file under the key of another file', async () => {
      const { stored, ref } = await sealed()
      const other = { ...ref, key: toBase64Url(newFileKey()) }

      await expect(openAttachment(other, stored)).rejects.toBeInstanceOf(CryptoError)
    })

    it('an entry that says another size', async () => {
      const { stored, ref } = await sealed()

      await expect(openAttachment({ ...ref, size: ref.size + 1 }, stored)).rejects.toThrow('not the size')
    })

    // 50 bytes are one chunk of 64 and one chunk of 128 alike, so the lengths agree and only the header tells them apart.
    it('an entry that says another chunk size, even when the length would agree', async () => {
      const small = await sealAttachment({ name: 'a', type: 'text/plain' }, bytesOf(50), { chunkSize: 64 })

      await expect(openAttachment({ ...small.ref, chunkSize: 128 }, small.stored)).rejects.toThrow('not cut')
    })

    it('a file that is another file of the same message', async () => {
      const one = await sealAttachment({ name: 'one', type: 'text/plain' }, bytesOf(200), { chunkSize: 64 })
      const two = await sealAttachment({ name: 'two', type: 'text/plain' }, bytesOf(200), { chunkSize: 64 })

      await expect(openAttachment(one.ref, two.stored)).rejects.toBeInstanceOf(CryptoError)
    })

    it('something that is not a file at all', async () => {
      const { ref } = await sealed()

      await expect(openAttachment(ref, new Uint8Array(encryptedFileLength(ref.size, ref.chunkSize)))).rejects.toBeInstanceOf(CryptoError)
    })
  })
})
