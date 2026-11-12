import { describe, expect, it } from 'vitest'
import {
  CryptoError,
  compare,
  concat,
  equal,
  fromBase64Url,
  fromHex,
  toBase64Url,
  toHex,
  u16,
  u32,
  utf8,
  uuidBytes,
  uuidFromBytes,
} from './bytes'
import { DEFAULT_ITERATIONS, openBackup, passphraseLength, sealBackup } from './backup'
import { epochCommitment, newEpochKey, openEnvelope, sealEnvelope, signEpoch, verifyEpoch, epochSignatureInput } from './epoch'
import { decryptChunk, decryptFile, encryptFile, encryptedFileLength, newFileKey, readFileHeader } from './file'
import { LABELS } from './formats'
import { createAccountSecrets, currentAccountKey, decodeSecrets, encodeSecrets, findAccountKey, openBundle, rotateAccountKey, signBundle } from './keys'
import { linkUri, normalizeLinkCode, openLinkSecrets, parseLinkUri, sealLinkSecrets } from './link'
import {
  MAX_MESSAGE_BLOB_BYTES,
  decodePayload,
  encodePayload,
  openMessage,
  pad,
  readMessageEpoch,
  sealMessage,
  unpad,
  type MessageContext,
} from './message'
import { newEd25519, newX25519 } from './primitives'
import { safetyNumber } from './safety'
import { openSealed, sealToPublicKey } from './sealedBox'

const A = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const B = '9d8c7b6a-5f4e-4d3c-8b2a-1f0e9d8c7b6a'
const CONVERSATION = 'c0ffee00-1234-4abc-8def-0123456789ab'
const MESSAGE = 'a1b2c3d4-e5f6-4789-8abc-def012345678'

// Fails the test unless the promise rejects with a CryptoError of the given kind.
async function rejectsWith(promise: Promise<unknown>, code: CryptoError['code']) {
  const error = await promise.then(
    () => undefined,
    (e: unknown) => e,
  )
  expect(error).toBeInstanceOf(CryptoError)
  expect((error as CryptoError).code).toBe(code)
}

function flip(bytes: Uint8Array, index: number): Uint8Array {
  const copy = new Uint8Array(bytes)
  copy[index] ^= 0x01
  return copy
}

describe('bytes', () => {
  it('reads a UUID as the bytes of its digits and back', () => {
    const bytes = uuidBytes(A)

    expect(toHex(bytes)).toBe('3f2a9c1e7b4d4e8a9c550a1b2c3d4e5f')
    expect(uuidFromBytes(bytes)).toBe(A)
    expect(toHex(uuidBytes(A.toUpperCase()))).toBe(toHex(bytes))
  })

  it.each([
    '',
    'not-a-uuid',
    '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5',
    '3f2a9c1e7b4d4e8a9c550a1b2c3d4e5f',
    '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5g',
    '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f0',
    '03f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f',
    // Extra digits in pairs would still be whole bytes, so only the anchors of the pattern refuse them.
    '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5fab',
    'ab3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f',
  ])(
    'refuses %j as a UUID',
    (text) => {
      expect(() => uuidBytes(text)).toThrow(CryptoError)
    },
  )

  it('round trips base64url without padding', () => {
    for (const length of [0, 1, 2, 3, 31, 32, 33]) {
      const bytes = Uint8Array.from({ length }, (_, i) => (i * 37 + 250) % 256)
      const text = toBase64Url(bytes)

      expect(text).not.toMatch(/[+/=]/)
      expect(toHex(fromBase64Url(text))).toBe(toHex(bytes))
    }
  })

  it('refuses text that is not base64url', () => {
    expect(() => fromBase64Url('ab+/')).toThrow(CryptoError)
    expect(() => fromBase64Url('abcde')).toThrow(CryptoError)
  })

  it('refuses a hex string of odd length or with other characters', () => {
    expect(() => fromHex('abc')).toThrow(CryptoError)
    expect(() => fromHex('zz')).toThrow(CryptoError)
  })

  it('writes 16-bit integers big-endian and refuses ones that do not fit', () => {
    expect(toHex(u16(0x0102))).toBe('0102')
    expect(toHex(u16(0xffff))).toBe('ffff')
    expect(() => u16(0x10000)).toThrow(CryptoError)
    expect(() => u16(-1)).toThrow(CryptoError)
  })

  it('writes integers big-endian and refuses ones that do not fit', () => {
    expect(toHex(u32(0x01020304))).toBe('01020304')
    expect(toHex(u32(0xffffffff))).toBe('ffffffff')
    expect(() => u32(-1)).toThrow(CryptoError)
    expect(() => u32(2 ** 32)).toThrow(CryptoError)
    expect(() => u32(1.5)).toThrow(CryptoError)
  })

  it('compares byte strings', () => {
    expect(equal(Uint8Array.of(1, 2), Uint8Array.of(1, 2))).toBe(true)
    expect(equal(Uint8Array.of(1, 2), Uint8Array.of(1, 3))).toBe(false)
    expect(equal(Uint8Array.of(1, 2), Uint8Array.of(3, 2))).toBe(false)
    expect(equal(Uint8Array.of(1, 2), Uint8Array.of(1, 2, 3))).toBe(false)
    expect(compare(Uint8Array.of(1, 2), Uint8Array.of(1, 3))).toBeLessThan(0)
    expect(compare(Uint8Array.of(2), Uint8Array.of(1, 9))).toBeGreaterThan(0)
    expect(compare(Uint8Array.of(1), Uint8Array.of(1, 0))).toBeLessThan(0)
  })
})

describe('padding', () => {
  it.each([
    [0, 256],
    [1, 256],
    [255, 256],
    [256, 512],
    [300, 512],
    [511, 512],
  ])('pads %i bytes to %i', (length, padded) => {
    const out = pad(new Uint8Array(length).fill(7))

    expect(out.length).toBe(padded)
    expect(unpad(out)).toEqual(new Uint8Array(length).fill(7))
  })

  it('keeps trailing zero bytes of the message itself', () => {
    const plain = Uint8Array.of(1, 0, 0)

    expect(unpad(pad(plain))).toEqual(plain)
  })

  it('refuses padding without its delimiter', () => {
    expect(() => unpad(new Uint8Array(256))).toThrow(CryptoError)
    expect(() => unpad(Uint8Array.of(1, 2, 3))).toThrow(CryptoError)
    expect(() => unpad(new Uint8Array(0))).toThrow(CryptoError)
  })
})

describe('message payload', () => {
  it('round trips text and files', () => {
    const payload = {
      text: 'café 😀',
      files: [{ name: 'a b.png', type: 'image/png', size: 12, key: toBase64Url(new Uint8Array(32)), chunkSize: 65536 }],
    }

    expect(decodePayload(encodePayload(payload))).toEqual(payload)
  })

  it.each([
    ['not JSON', 'nope'],
    ['an array', '[]'],
    ['no text', '{"files":[]}'],
    ['no files', '{"text":"x"}'],
    ['a file without a name', '{"text":"x","files":[{"type":"a","size":1,"key":"","chunkSize":1}]}'],
    ['a file with a negative size', `{"text":"x","files":[{"name":"n","type":"a","size":-1,"key":"${toBase64Url(new Uint8Array(32))}","chunkSize":1}]}`],
    ['a file key of the wrong length', '{"text":"x","files":[{"name":"n","type":"a","size":1,"key":"AAAA","chunkSize":1}]}'],
    ['a file with chunk size 0', `{"text":"x","files":[{"name":"n","type":"a","size":1,"key":"${toBase64Url(new Uint8Array(32))}","chunkSize":0}]}`],
    ['a file with a fractional size', `{"text":"x","files":[{"name":"n","type":"a","size":1.5,"key":"${toBase64Url(new Uint8Array(32))}","chunkSize":1}]}`],
    ['a file with a chunk size above the largest', `{"text":"x","files":[{"name":"n","type":"a","size":1,"key":"${toBase64Url(new Uint8Array(32))}","chunkSize":16777217}]}`],
    ['a file with a name that is too long', `{"text":"x","files":[{"name":"${'n'.repeat(256)}","type":"a","size":1,"key":"${toBase64Url(new Uint8Array(32))}","chunkSize":1}]}`],
  ])('refuses %s', (_name, json) => {
    expect(() => decodePayload(utf8(json))).toThrow(CryptoError)
  })

  it('accepts the largest chunk size and the longest name', () => {
    const files = [{ name: 'n'.repeat(255), type: 'a', size: 1, key: toBase64Url(new Uint8Array(32)), chunkSize: 16777216 }]

    expect(decodePayload(utf8(JSON.stringify({ text: 'x', files })))).toEqual({ text: 'x', files })
  })

  it('refuses text that is not valid UTF-8 instead of repairing it', () => {
    const bytes = concat(utf8('{"text":"'), Uint8Array.of(0xff), utf8('","files":[]}'))

    expect(() => decodePayload(bytes)).toThrow(CryptoError)
  })

  it('ignores fields it does not know', () => {
    expect(decodePayload(utf8('{"text":"x","files":[],"later":1}'))).toEqual({ text: 'x', files: [] })
  })
})

describe('message', () => {
  const context: MessageContext = { conversationId: CONVERSATION, senderId: A, clientMessageId: MESSAGE, editCount: 0 }
  const payload = { text: 'hello', files: [] }

  async function sealed() {
    const signer = await newEd25519()
    const epochKey = newEpochKey()
    const blob = await sealMessage({ context, epoch: 7, epochKey, payload, signer })
    return { signer, epochKey, blob }
  }

  it('round trips and reports its epoch', async () => {
    const { signer, epochKey, blob } = await sealed()

    expect(readMessageEpoch(blob)).toBe(7)
    expect(await openMessage({ context, epochKey, signerPublic: signer.publicKey, blob })).toEqual(payload)
  })

  it('is different every time it is sealed', async () => {
    const signer = await newEd25519()
    const epochKey = newEpochKey()
    const input = { context, epoch: 1, epochKey, payload, signer }

    expect(toHex(await sealMessage(input))).not.toBe(toHex(await sealMessage(input)))
  })

  it('hides the length of short messages', async () => {
    const signer = await newEd25519()
    const epochKey = newEpochKey()
    const short = await sealMessage({ context, epoch: 1, epochKey, payload: { text: 'a', files: [] }, signer })
    const longer = await sealMessage({ context, epoch: 1, epochKey, payload: { text: 'a'.repeat(200), files: [] }, signer })

    expect(short.length).toBe(longer.length)
  })

  // Every region of the blob is covered: the header by the associated data and the signature, the
  // ciphertext and its tag by both, the signature by itself.
  it.each([
    ['the version', 0],
    ['the suite', 1],
    ['the epoch', 3],
    ['the nonce', 10],
    ['the ciphertext', 30],
    ['the last byte of the tag', -65],
    ['the signature', -1],
  ])('refuses a blob with %s changed', async (_name, index) => {
    const { signer, epochKey, blob } = await sealed()
    const at = index < 0 ? blob.length + index : index

    const result = openMessage({ context, epochKey, signerPublic: signer.publicKey, blob: flip(blob, at) })

    await expect(result).rejects.toBeInstanceOf(CryptoError)
  })

  it.each([
    ['another conversation', { conversationId: B }],
    ['another sender', { senderId: B }],
    ['another client message id', { clientMessageId: CONVERSATION }],
    ['another edit count', { editCount: 1 }],
  ])('refuses a blob presented for %s', async (_name, change) => {
    const { signer, epochKey, blob } = await sealed()

    await rejectsWith(openMessage({ context: { ...context, ...change }, epochKey, signerPublic: signer.publicKey, blob }), 'authentication')
  })

  it('refuses a message signed by someone other than the sender', async () => {
    const { epochKey, blob } = await sealed()
    const other = await newEd25519()

    await rejectsWith(openMessage({ context, epochKey, signerPublic: other.publicKey, blob }), 'authentication')
  })

  it('refuses the wrong epoch key even when the signature is right', async () => {
    const { signer, blob } = await sealed()

    await rejectsWith(openMessage({ context, epochKey: newEpochKey(), signerPublic: signer.publicKey, blob }), 'authentication')
  })

  it('refuses a message that is too short, truncated, or from a later version', async () => {
    const { signer, epochKey, blob } = await sealed()
    const open = (candidate: Uint8Array) => openMessage({ context, epochKey, signerPublic: signer.publicKey, blob: candidate })

    await rejectsWith(open(blob.subarray(0, 40)), 'malformed')
    await expect(open(blob.subarray(0, blob.length - 1))).rejects.toBeInstanceOf(CryptoError)
    await rejectsWith(open(Uint8Array.of(2, ...blob.subarray(1))), 'unsupported')
    await rejectsWith(open(Uint8Array.of(...blob.subarray(0, 1), 9, ...blob.subarray(2))), 'unsupported')
  })

  it('refuses a message over the size limit', async () => {
    const signer = await newEd25519()

    await rejectsWith(
      sealMessage({ context, epoch: 1, epochKey: newEpochKey(), payload: { text: 'x'.repeat(MAX_MESSAGE_BLOB_BYTES), files: [] }, signer }),
      'malformed',
    )
  })
})

describe('sealed box', () => {
  const context = utf8('context')

  it('opens only for the recipient, with the same label and context', async () => {
    const recipient = await newX25519()
    const box = await sealToPublicKey('chatnet/v1/test', context, recipient.publicKey, utf8('secret'))

    expect(toHex(await openSealed('chatnet/v1/test', context, recipient, box))).toBe(toHex(utf8('secret')))
    await rejectsWith(openSealed('chatnet/v1/other', context, recipient, box), 'authentication')
    await rejectsWith(openSealed('chatnet/v1/test', utf8('contexT'), recipient, box), 'authentication')
    await rejectsWith(openSealed('chatnet/v1/test', context, await newX25519(), box), 'authentication')
  })

  it('refuses a box with any byte changed', async () => {
    const recipient = await newX25519()
    const box = await sealToPublicKey('chatnet/v1/test', context, recipient.publicKey, utf8('secret'))

    for (const index of [0, 1, 2, 33, 34, 45, 46, box.length - 1]) {
      await expect(openSealed('chatnet/v1/test', context, recipient, flip(box, index))).rejects.toBeInstanceOf(CryptoError)
    }
  })

  it('refuses a box that is too short, and opens the shortest possible one', async () => {
    const recipient = await newX25519()
    const empty = await sealToPublicKey('chatnet/v1/test', context, recipient.publicKey, new Uint8Array(0))

    expect(empty.length).toBe(62)
    expect((await openSealed('chatnet/v1/test', context, recipient, empty)).length).toBe(0)
    await rejectsWith(openSealed('chatnet/v1/test', context, recipient, empty.subarray(0, 61)), 'malformed')
  })

  it('names a box from another version, not just a box that fails to open', async () => {
    const recipient = await newX25519()
    const box = await sealToPublicKey('chatnet/v1/test', context, recipient.publicKey, utf8('secret'))

    await rejectsWith(openSealed('chatnet/v1/test', context, recipient, flip(box, 0)), 'unsupported')
    await rejectsWith(openSealed('chatnet/v1/test', context, recipient, flip(box, 1)), 'unsupported')
  })
})

describe('epoch key envelope', () => {
  const target = { conversationId: CONVERSATION, epoch: 4, userId: B, generation: 2 }

  it('opens for its member and is bound to every field of its target', async () => {
    const account = await newX25519()
    const epochKey = newEpochKey()
    const envelope = await sealEnvelope(target, account.publicKey, epochKey)

    expect(toHex(await openEnvelope(target, account, envelope))).toBe(toHex(epochKey))
    for (const change of [{ conversationId: A }, { epoch: 5 }, { userId: A }, { generation: 3 }]) {
      await rejectsWith(openEnvelope({ ...target, ...change }, account, envelope), 'authentication')
    }
  })

  it('does not open with the account key of another member', async () => {
    const envelope = await sealEnvelope(target, (await newX25519()).publicKey, newEpochKey())

    await rejectsWith(openEnvelope(target, await newX25519(), envelope), 'authentication')
  })

  it('refuses an epoch key that is not 32 bytes, going in or coming out', async () => {
    const account = await newX25519()
    const context = concat(uuidBytes(target.conversationId), u32(target.epoch), uuidBytes(target.userId), u32(target.generation))
    const short = await sealToPublicKey(LABELS.envelope, context, account.publicKey, new Uint8Array(31))

    expect(() => sealEnvelope(target, new Uint8Array(32), new Uint8Array(31))).toThrow(CryptoError)
    await rejectsWith(openEnvelope(target, account, short), 'malformed')
  })

  it('has a commitment that depends on the key, the conversation and the epoch', async () => {
    const key = newEpochKey()
    const base = toHex(await epochCommitment(CONVERSATION, 1, key))

    expect(base).toBe(toHex(await epochCommitment(CONVERSATION, 1, key)))
    expect(base).not.toBe(toHex(await epochCommitment(CONVERSATION, 2, key)))
    expect(base).not.toBe(toHex(await epochCommitment(A, 1, key)))
    expect(base).not.toBe(toHex(await epochCommitment(CONVERSATION, 1, newEpochKey())))
  })
})

describe('epoch record signature', () => {
  const members = [
    { userId: A, generation: 1 },
    { userId: B, generation: 2 },
  ]

  async function signed() {
    const creator = await newEd25519()
    const record = { conversationId: CONVERSATION, epoch: 2, creatorId: A, commitment: new Uint8Array(32).fill(5), members }
    return { creator, record, signature: await signEpoch(record, creator) }
  }

  it('fails when any field of the record changes', async () => {
    const { creator, record, signature } = await signed()
    const changes = [
      { conversationId: B },
      { epoch: 3 },
      { creatorId: B },
      { commitment: new Uint8Array(32).fill(6) },
      { members: [members[0]] },
      { members: [members[0], { ...members[1], generation: 3 }] },
      { members: [...members, { userId: CONVERSATION, generation: 1 }] },
    ]

    expect(await verifyEpoch(record, creator.publicKey, signature)).toBe(true)
    for (const change of changes) expect(await verifyEpoch({ ...record, ...change }, creator.publicKey, signature)).toBe(false)
  })

  it('fails under another creator key', async () => {
    const { record, signature } = await signed()

    expect(await verifyEpoch(record, (await newEd25519()).publicKey, signature)).toBe(false)
  })

  it('refuses a record that names a member twice or has a bad commitment', async () => {
    const { record } = await signed()

    expect(() => epochSignatureInput({ ...record, members: [members[0], members[0]] })).toThrow(CryptoError)
    expect(() => epochSignatureInput({ ...record, commitment: new Uint8Array(31) })).toThrow(CryptoError)
  })
})

describe('file', () => {
  const key = newFileKey()
  const plain = Uint8Array.from({ length: 300 }, (_, i) => i % 256)

  it('round trips at sizes around the chunk boundary', async () => {
    for (const length of [0, 1, 63, 64, 65, 128, 129, 300]) {
      const part = plain.subarray(0, length)
      const stored = await encryptFile(key, part, { chunkSize: 64 })

      expect(stored.length).toBe(encryptedFileLength(length, 64))
      expect(toHex(await decryptFile(key, stored))).toBe(toHex(part))
    }
  })

  it('uses 64 KiB chunks by default', async () => {
    const stored = await encryptFile(key, new Uint8Array(70_000))

    expect(readFileHeader(stored).chunkSize).toBe(65536)
    expect(stored.length).toBe(13 + 70_000 + 2 * 16)
  })

  it('is different every time (the nonce prefix is random)', async () => {
    expect(toHex(await encryptFile(key, plain))).not.toBe(toHex(await encryptFile(key, plain)))
  })

  describe('refuses damage', () => {
    async function stored() {
      return encryptFile(key, plain, { chunkSize: 64 })
    }
    const stride = 64 + 16

    it('a changed byte anywhere', async () => {
      const file = await stored()

      for (const index of [0, 1, 2, 5, 6, 12, 13, 40, 13 + stride, file.length - 1]) {
        await expect(decryptFile(key, flip(file, index))).rejects.toBeInstanceOf(CryptoError)
      }
    })

    it('chunks swapped', async () => {
      const file = await stored()
      const swapped = concat(file.subarray(0, 13), file.subarray(13 + stride, 13 + 2 * stride), file.subarray(13, 13 + stride), file.subarray(13 + 2 * stride))

      await rejectsWith(decryptFile(key, swapped), 'authentication')
    })

    it('the last chunk dropped, however the cut falls', async () => {
      const file = await stored()
      const lastStart = 13 + 4 * stride

      await rejectsWith(decryptFile(key, file.subarray(0, lastStart)), 'authentication')
      await rejectsWith(decryptFile(key, file.subarray(0, 13 + 3 * stride)), 'authentication')
    })

    it('a chunk appended', async () => {
      const file = await stored()

      await rejectsWith(decryptFile(key, concat(file, file.subarray(13, 13 + stride))), 'authentication')
    })

    it('a chunk moved into another file with the same key', async () => {
      const file = await stored()
      const other = await encryptFile(key, plain, { chunkSize: 64 })
      const spliced = concat(file.subarray(0, 13 + stride), other.subarray(13 + stride, 13 + 2 * stride), file.subarray(13 + 2 * stride))

      await rejectsWith(decryptFile(key, spliced), 'authentication')
    })

    it('the wrong key', async () => {
      await rejectsWith(decryptFile(newFileKey(), await stored()), 'authentication')
    })

    it('a header that is cut off, from a later version, or with a bad chunk size', async () => {
      const file = await stored()

      await rejectsWith(decryptFile(key, file.subarray(0, 12)), 'malformed')
      await rejectsWith(decryptFile(key, Uint8Array.of(2, ...file.subarray(1))), 'unsupported')
      await rejectsWith(decryptFile(key, concat(file.subarray(0, 2), u32(0), file.subarray(6))), 'malformed')
      await rejectsWith(decryptFile(key, concat(file.subarray(0, 2), u32(2 ** 24 + 1), file.subarray(6))), 'malformed')
    })

    it('a body that is empty or ends in a stub shorter than a tag', async () => {
      const file = await stored()

      await rejectsWith(decryptFile(key, file.subarray(0, 13)), 'malformed')
      await rejectsWith(decryptFile(key, file.subarray(0, 13 + 3 * stride + 5)), 'malformed')
    })

    it('a chunk opened as the last one when it is not, and the reverse', async () => {
      const file = await stored()
      const header = readFileHeader(file)
      const first = file.subarray(13, 13 + stride)

      await rejectsWith(decryptChunk(key, header, 0, true, first), 'authentication')
      await rejectsWith(decryptChunk(key, header, 1, false, first), 'authentication')
      expect((await decryptChunk(key, header, 0, false, first)).length).toBe(64)
    })
  })

  it('refuses a chunk size out of range', async () => {
    await rejectsWith(encryptFile(key, plain, { chunkSize: 0 }), 'malformed')
    await rejectsWith(encryptFile(key, plain, { chunkSize: 16 * 1024 * 1024 + 1 }), 'malformed')
  })
})

describe('backup', () => {
  const passphrase = 'a long enough passphrase'
  const cheap = { iterations: 1000 }

  it('round trips and keeps every account key generation', async () => {
    const secrets = await rotateAccountKey(await createAccountSecrets())
    const blob = await sealBackup(A, passphrase, secrets, cheap)
    const opened = await openBackup(A, passphrase, blob)

    expect(toHex(encodeSecrets(opened))).toBe(toHex(encodeSecrets(secrets)))
  })

  it('refuses a fractional iteration count', async () => {
    await rejectsWith(sealBackup(A, passphrase, await createAccountSecrets(), { iterations: 1000.5 }), 'malformed')
  })

  it('refuses a wrong passphrase, another user, and any changed byte', async () => {
    const blob = await sealBackup(A, passphrase, await createAccountSecrets(), cheap)

    await rejectsWith(openBackup(A, 'a different passphrase', blob), 'authentication')
    await rejectsWith(openBackup(B, passphrase, blob), 'authentication')
    for (const index of [3, 6, 7, 22, 23, 34, 35, blob.length - 1]) {
      await expect(openBackup(A, passphrase, flip(blob, index))).rejects.toBeInstanceOf(CryptoError)
    }
  })

  it('refuses a passphrase under 12 characters, counted after normalisation', async () => {
    const secrets = await createAccountSecrets()

    await rejectsWith(sealBackup(A, 'only eleven', secrets, cheap), 'malformed')
    await sealBackup(A, 'twelve chars', secrets, cheap)
    // Twelve code points that normalise to 18 characters count as 18, and twelve that stay 12 count as 12.
    expect(passphraseLength('ﬁﬁﬁﬁﬁﬁ')).toBe(12)
    expect(passphraseLength('😀'.repeat(12))).toBe(12)
  })

  it('refuses iteration counts out of range, when sealing and when opening', async () => {
    const secrets = await createAccountSecrets()
    const blob = await sealBackup(A, passphrase, secrets, cheap)

    await rejectsWith(sealBackup(A, passphrase, secrets, { iterations: 0 }), 'malformed')
    await rejectsWith(sealBackup(A, passphrase, secrets, { iterations: 10_000_001 }), 'malformed')
    await rejectsWith(openBackup(A, passphrase, concat(blob.subarray(0, 3), u32(0), blob.subarray(7))), 'malformed')
    await rejectsWith(openBackup(A, passphrase, concat(blob.subarray(0, 3), u32(10_000_001), blob.subarray(7))), 'malformed')
  })

  it('refuses a blob that is too short or from a later version', async () => {
    const blob = await sealBackup(A, passphrase, await createAccountSecrets(), cheap)

    await rejectsWith(openBackup(A, passphrase, blob.subarray(0, 35)), 'malformed')
    await rejectsWith(openBackup(A, passphrase, Uint8Array.of(2, ...blob.subarray(1))), 'unsupported')
    await rejectsWith(openBackup(A, passphrase, concat(blob.subarray(0, 2), Uint8Array.of(9), blob.subarray(3))), 'unsupported')
  })

  it('stretches with 600000 iterations unless told otherwise', () => {
    expect(DEFAULT_ITERATIONS).toBe(600_000)
  })
})

describe('account keys', () => {
  it('start at generation 1 and rotate to the next, keeping the old ones and the identity', async () => {
    const first = await createAccountSecrets()
    const second = await rotateAccountKey(first)

    expect(first.accounts.map((key) => key.generation)).toEqual([1])
    expect(second.accounts.map((key) => key.generation)).toEqual([1, 2])
    expect(currentAccountKey(second).generation).toBe(2)
    expect(findAccountKey(second, 1)).toBe(second.accounts[0])
    expect(findAccountKey(second, 5)).toBeUndefined()
    expect(second.identity).toBe(first.identity)
    expect(toHex(second.accounts[0].privateKey)).not.toBe(toHex(second.accounts[1].privateKey))
  })

  it('encode and decode', async () => {
    const secrets = await rotateAccountKey(await createAccountSecrets())
    const decoded = decodeSecrets(encodeSecrets(secrets))

    expect(toHex(encodeSecrets(decoded))).toBe(toHex(encodeSecrets(secrets)))
  })

  it('refuse damaged secrets', async () => {
    const bytes = encodeSecrets(await rotateAccountKey(await createAccountSecrets()))

    expect(() => decodeSecrets(bytes.subarray(0, 40))).toThrow(CryptoError)
    expect(() => decodeSecrets(bytes.subarray(0, bytes.length - 1))).toThrow(CryptoError)
    expect(() => decodeSecrets(concat(bytes, Uint8Array.of(0)))).toThrow(CryptoError)
    expect(() => decodeSecrets(Uint8Array.of(2, ...bytes.subarray(1)))).toThrow(CryptoError)
    expect(() => decodeSecrets(concat(bytes.subarray(0, 65), Uint8Array.of(0, 0), bytes.subarray(67)))).toThrow(CryptoError)
  })

  it('refuse secrets that hold no account key', async () => {
    const secrets = await createAccountSecrets()
    const bytes = encodeSecrets(secrets)

    expect(() => decodeSecrets(concat(bytes.subarray(0, 65), Uint8Array.of(0, 0)))).toThrow(CryptoError)
    expect(() => encodeSecrets({ identity: secrets.identity, accounts: [] })).toThrow(CryptoError)
  })

  it('refuse generations that do not ascend', async () => {
    const secrets = await rotateAccountKey(await createAccountSecrets())
    secrets.accounts[1].generation = 1

    expect(() => decodeSecrets(encodeSecrets(secrets))).toThrow(CryptoError)
  })
})

describe('bundle', () => {
  it('is bound to the user it was made for', async () => {
    const secrets = await createAccountSecrets()
    const wire = await signBundle(A, secrets.identity, secrets.accounts[0])

    expect((await openBundle(A, wire)).generation).toBe(1)
    await rejectsWith(openBundle(B, wire), 'authentication')
  })

  it('refuses any changed byte, a wrong length and a later version', async () => {
    const secrets = await createAccountSecrets()
    const wire = await signBundle(A, secrets.identity, secrets.accounts[0])

    for (const index of [2, 5, 6, 38, 70, wire.length - 1]) await expect(openBundle(A, flip(wire, index))).rejects.toBeInstanceOf(CryptoError)
    await rejectsWith(openBundle(A, wire.subarray(0, 100)), 'malformed')
    await rejectsWith(openBundle(A, Uint8Array.of(2, ...wire.subarray(1))), 'unsupported')
  })
})

describe('device link', () => {
  it('delivers the secrets only for the same request and person', async () => {
    const temporary = await newX25519()
    const secrets = await createAccountSecrets()
    const sealed = await sealLinkSecrets(MESSAGE, A, temporary.publicKey, secrets)

    expect(toHex(encodeSecrets(await openLinkSecrets(MESSAGE, A, temporary, sealed)))).toBe(toHex(encodeSecrets(secrets)))
    await rejectsWith(openLinkSecrets(CONVERSATION, A, temporary, sealed), 'authentication')
    await rejectsWith(openLinkSecrets(MESSAGE, B, temporary, sealed), 'authentication')
    await rejectsWith(openLinkSecrets(MESSAGE, A, await newX25519(), sealed), 'authentication')
  })

  it('accepts only a code of exactly ten characters from the alphabet', () => {
    expect(normalizeLinkCode('ABC')).toBeUndefined()
    expect(normalizeLinkCode('ABCDEFGHJKM')).toBeUndefined()
    expect(normalizeLinkCode('ABCDEFGHJK')).toBe('ABCDEFGHJK')
    expect(normalizeLinkCode('ABCDEFGHJ!')).toBeUndefined()
  })

  it('refuses an address whose request id is not a UUID', async () => {
    const key = toBase64Url((await newX25519()).publicKey)

    expect(() => parseLinkUri(`chatnet-link:1:${'-'.repeat(36)}:${key}`)).toThrow(CryptoError)
  })

  it('writes and reads the address of a QR code, and refuses others', async () => {
    const temporary = await newX25519()
    const uri = linkUri(MESSAGE, temporary.publicKey)

    expect(parseLinkUri(uri).requestId).toBe(MESSAGE)
    for (const bad of ['', 'chatnet-link:2:' + uri.slice(15), uri.slice(0, -1), uri + 'A', 'https://example.com', uri.replace(MESSAGE, 'x')]) {
      expect(() => parseLinkUri(bad)).toThrow(CryptoError)
    }
  })
})

describe('safety number', () => {
  it('changes when either identity key or either user id changes', async () => {
    const keyA = (await newEd25519()).publicKey
    const keyB = (await newEd25519()).publicKey
    const base = await safetyNumber({ userId: A, identityPublic: keyA }, { userId: B, identityPublic: keyB })

    expect(await safetyNumber({ userId: A, identityPublic: (await newEd25519()).publicKey }, { userId: B, identityPublic: keyB })).not.toBe(base)
    expect(await safetyNumber({ userId: A, identityPublic: keyA }, { userId: B, identityPublic: (await newEd25519()).publicKey })).not.toBe(base)
    expect(await safetyNumber({ userId: CONVERSATION, identityPublic: keyA }, { userId: B, identityPublic: keyB })).not.toBe(base)
    expect(await safetyNumber({ userId: B, identityPublic: keyB }, { userId: A, identityPublic: keyA })).toBe(base)
  })
})
