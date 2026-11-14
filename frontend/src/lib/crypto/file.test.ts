import { describe, expect, it } from 'vitest'
import { CryptoError, type Bytes } from './bytes'
import { decryptFileBlob, encryptFile, encryptFileBlob, newFileKey } from './file'

function bytesOf(length: number): Bytes {
  const bytes = new Uint8Array(length)
  for (let i = 0; i < length; i++) bytes[i] = (i * 13 + 5) % 256
  return bytes
}
const read = async (blob: Blob) => new Uint8Array(await blob.arrayBuffer())

// Where two byte strings first differ, or -1: a failure of a file of ten mebibytes has to say where, not print it.
function firstDifference(a: Uint8Array, b: Uint8Array): number {
  if (a.length !== b.length) return Math.min(a.length, b.length)
  return a.findIndex((byte, i) => byte !== b[i])
}

const PREFIX = Uint8Array.from([1, 2, 3, 4, 5, 6, 7])

// A file of 4 chunks of a mebibyte is one slice; this one is cut into three (4, 4 and 2 chunks), the last chunk short.
const MEBIBYTE = 1024 * 1024
const SLICED = { chunkSize: MEBIBYTE, length: 9 * MEBIBYTE + 5 }

describe('sealing a Blob a slice at a time', () => {
  it.each([0, 1, 63, 64, 65, 200])('makes the stored file that sealing the bytes in one piece makes, for %i bytes', async (length) => {
    const key = newFileKey()
    const plain = bytesOf(length)
    const overrides = { chunkSize: 64, noncePrefix: PREFIX }

    expect(await read(await encryptFileBlob(key, new Blob([plain]), overrides))).toEqual(await encryptFile(key, plain, overrides))
  })

  it('makes that same file when it takes several slices, with a short last chunk', async () => {
    const key = newFileKey()
    const plain = bytesOf(SLICED.length)
    const overrides = { chunkSize: SLICED.chunkSize, noncePrefix: PREFIX }

    const sealed = await read(await encryptFileBlob(key, new Blob([plain]), overrides))
    const whole = await encryptFile(key, plain, overrides)

    expect(sealed.length).toBe(whole.length)
    expect(firstDifference(sealed, whole)).toBe(-1)
  })

  it('refuses a chunk size out of range', async () => {
    await expect(encryptFileBlob(newFileKey(), new Blob([bytesOf(10)]), { chunkSize: 0 })).rejects.toBeInstanceOf(CryptoError)
  })
})

describe('opening a stored file as a Blob a slice at a time', () => {
  it.each([1, 63, 64, 65, 200])('gives back what sealing in one piece was given, for %i bytes', async (length) => {
    const key = newFileKey()
    const plain = bytesOf(length)

    const stored = await encryptFile(key, plain, { chunkSize: 64 })

    expect(await read(await decryptFileBlob(key, new Blob([stored])))).toEqual(plain)
  })

  it('gives back a file that takes several slices', async () => {
    const key = newFileKey()
    const plain = bytesOf(SLICED.length)

    const stored = await encryptFile(key, plain, { chunkSize: SLICED.chunkSize })

    const opened = await read(await decryptFileBlob(key, new Blob([stored])))

    expect(opened.length).toBe(plain.length)
    expect(firstDifference(opened, plain)).toBe(-1)
  })

  describe('refuses, in a slice after the first as in the first,', () => {
    async function sliced() {
      const key = newFileKey()
      const stored = await encryptFile(key, bytesOf(SLICED.length), { chunkSize: SLICED.chunkSize })
      return { key, stored }
    }

    it('a byte changed', async () => {
      const { key, stored } = await sliced()
      const damaged: Bytes = new Uint8Array(stored)
      damaged[stored.length - 3] ^= 1

      await expect(decryptFileBlob(key, new Blob([damaged]))).rejects.toBeInstanceOf(CryptoError)
    })

    it('a file that lost its last chunk, which would otherwise end on a chunk that is not marked as the last', async () => {
      const { key, stored } = await sliced()
      const cut = stored.subarray(0, stored.length - (5 + 16))

      await expect(decryptFileBlob(key, new Blob([cut]))).rejects.toBeInstanceOf(CryptoError)
    })

    it('a file under another key', async () => {
      const { stored } = await sliced()

      await expect(decryptFileBlob(newFileKey(), new Blob([stored]))).rejects.toBeInstanceOf(CryptoError)
    })
  })

  it('refuses a file that is too short to have a header, and one with a header and no chunk', async () => {
    const key = newFileKey()
    const stored = await encryptFile(key, bytesOf(10), { chunkSize: 64 })

    await expect(decryptFileBlob(key, new Blob([stored.subarray(0, 5)]))).rejects.toThrow('too short')
    await expect(decryptFileBlob(key, new Blob([stored.subarray(0, 13)]))).rejects.toThrow('no chunks')
  })
})
