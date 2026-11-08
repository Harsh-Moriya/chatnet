import { describe, expect, it } from 'vitest'
import vectorsText from '../../../../docs/architecture/encryption-test-vectors.json?raw'
import { fromHex, toHex, uuidBytes } from './bytes'
import { openBackup, sealBackup } from './backup'
import { epochCommitment, epochSignatureInput, openEnvelope, sealEnvelope, signEpoch, verifyEpoch } from './epoch'
import { chunkOffset, decryptChunk, decryptFile, encryptFile, encryptedFileLength, readFileHeader } from './file'
import {
  bundleSignatureInput,
  decodeSecrets,
  encodeSecrets,
  openBundle,
  signBundle,
  type AccountKey,
  type AccountSecrets,
} from './keys'
import { formatLinkCode, linkCode, linkUri, matchesLinkCode, normalizeLinkCode, openLinkSecrets, parseLinkUri, sealLinkSecrets } from './link'
import { messageSignatureInput, openMessage, readMessageEpoch, sealMessage, type MessageContext } from './message'
import { ed25519FromSeed, x25519FromPrivate, type KeyPair } from './primitives'
import { formatSafetyNumber, safetyNumber } from './safety'

interface KeyEntry {
  private: string
  public: string
}

interface Vectors {
  keys: Record<'identityA' | 'identityB', KeyEntry> & Record<'accountA1' | 'accountA2' | 'accountB1', KeyEntry & { generation: number }>
  bundle: { userId: string; generation: number; signatureInput: string; wire: string }
  secrets: { encoded: string }
  backup: { userId: string; passphrase: string; normalizedPassphrase: string; iterations: number; salt: string; nonce: string; blob: string }
  epoch: {
    conversationId: string
    epoch: number
    epochKey: string
    commitment: string
    envelope: { userId: string; generation: number; ephemeralPrivate: string; nonce: string; sealed: string }
    record: { creatorId: string; members: { userId: string; generation: number }[]; signatureInput: string; signature: string }
  }
  messages: {
    description: string
    context: MessageContext
    epoch: number
    nonce: string
    payloadJson: string
    paddedLength: number
    signatureInput: string
    blob: string
  }[]
  files: { description: string; key: string; noncePrefix: string; chunkSize: number; plain: string; stored: string }[]
  link: {
    requestId: string
    userId: string
    temporary: KeyEntry
    code: string
    uri: string
    ephemeralPrivate: string
    nonce: string
    sealed: string
  }
  safetyNumber: { a: { userId: string; identityPublic: string }; b: { userId: string; identityPublic: string }; number: string }
}

// Read as text and parsed here, so the type check of the build does not need the file beside the
// frontend sources.
const vectors = JSON.parse(vectorsText) as Vectors

// The vectors were written by a separate implementation of the specification on OpenSSL, sharing no
// code with the library. Everything the library produces here must match them byte for byte, and
// everything they contain must open.

const pairOf = (entry: { private: string; public: string }): KeyPair => ({ privateKey: fromHex(entry.private), publicKey: fromHex(entry.public) })
const accountOf = (entry: { private: string; public: string; generation: number }): AccountKey => ({ ...pairOf(entry), generation: entry.generation })

const identityA = pairOf(vectors.keys.identityA)
const identityB = pairOf(vectors.keys.identityB)
const accountB1 = accountOf(vectors.keys.accountB1)
const secretsA: AccountSecrets = { identity: identityA, accounts: [accountOf(vectors.keys.accountA1), accountOf(vectors.keys.accountA2)] }

describe('keys', () => {
  it.each([
    ['identityA', vectors.keys.identityA, ed25519FromSeed],
    ['identityB', vectors.keys.identityB, ed25519FromSeed],
    ['accountA1', vectors.keys.accountA1, x25519FromPrivate],
    ['accountA2', vectors.keys.accountA2, x25519FromPrivate],
    ['accountB1', vectors.keys.accountB1, x25519FromPrivate],
  ])('derives the public key of %s', async (_name, entry, derive) => {
    expect(toHex((await derive(fromHex(entry.private))).publicKey)).toBe(entry.public)
  })
})

describe('account bundle', () => {
  const { userId, generation, signatureInput, wire } = vectors.bundle

  it('signs to the published bytes', async () => {
    expect(toHex(await signBundle(userId, identityA, secretsA.accounts[1]))).toBe(wire)
  })

  it('has the published signature input', () => {
    const opened = {
      userId,
      generation,
      identityPublic: identityA.publicKey,
      accountPublic: secretsA.accounts[1].publicKey,
    }

    expect(toHex(bundleSignatureInput(opened))).toBe(signatureInput)
  })

  it('opens', async () => {
    const bundle = await openBundle(userId, fromHex(wire))

    expect(bundle.generation).toBe(generation)
    expect(toHex(bundle.identityPublic)).toBe(vectors.keys.identityA.public)
    expect(toHex(bundle.accountPublic)).toBe(vectors.keys.accountA2.public)
  })
})

describe('secrets', () => {
  it('encode to the published bytes and decode back', () => {
    expect(toHex(encodeSecrets(secretsA))).toBe(vectors.secrets.encoded)

    const decoded = decodeSecrets(fromHex(vectors.secrets.encoded))
    expect(toHex(decoded.identity.privateKey)).toBe(vectors.keys.identityA.private)
    expect(decoded.accounts.map((account) => account.generation)).toEqual([1, 2])
    expect(toHex(decoded.accounts[1].privateKey)).toBe(vectors.keys.accountA2.private)
  })
})

describe('backup', () => {
  const backup = vectors.backup
  const overrides = { salt: fromHex(backup.salt), nonce: fromHex(backup.nonce), iterations: backup.iterations }

  it('seals to the published bytes', async () => {
    expect(toHex(await sealBackup(backup.userId, backup.passphrase, secretsA, overrides))).toBe(backup.blob)
  })

  it('opens with the passphrase', async () => {
    const opened = await openBackup(backup.userId, backup.passphrase, fromHex(backup.blob))

    expect(toHex(encodeSecrets(opened))).toBe(vectors.secrets.encoded)
  })

  // The passphrase is normalised to NFKC first, so the ligature and its spelled-out form agree.
  it('treats a passphrase and its NFKC form alike', async () => {
    expect(backup.normalizedPassphrase).not.toBe(backup.passphrase)

    const opened = await openBackup(backup.userId, backup.normalizedPassphrase, fromHex(backup.blob))

    expect(toHex(encodeSecrets(opened))).toBe(vectors.secrets.encoded)
  })
})

describe('epoch key', () => {
  const epoch = vectors.epoch
  const target = { conversationId: epoch.conversationId, epoch: epoch.epoch, userId: epoch.envelope.userId, generation: epoch.envelope.generation }

  it('has the published commitment', async () => {
    expect(toHex(await epochCommitment(epoch.conversationId, epoch.epoch, fromHex(epoch.epochKey)))).toBe(epoch.commitment)
  })

  it('seals an envelope to the published bytes', async () => {
    const sealed = await sealEnvelope(target, accountB1.publicKey, fromHex(epoch.epochKey), {
      ephemeralPrivate: fromHex(epoch.envelope.ephemeralPrivate),
      nonce: fromHex(epoch.envelope.nonce),
    })

    expect(toHex(sealed)).toBe(epoch.envelope.sealed)
    expect(sealed.length).toBe(94)
  })

  it('opens the published envelope', async () => {
    expect(toHex(await openEnvelope(target, accountB1, fromHex(epoch.envelope.sealed)))).toBe(epoch.epochKey)
  })

  describe('signed record', () => {
    const record = {
      conversationId: epoch.conversationId,
      epoch: epoch.epoch,
      creatorId: epoch.record.creatorId,
      commitment: fromHex(epoch.commitment),
      members: epoch.record.members,
    }

    it('has the published signature input, whatever order the members are given in', () => {
      expect(toHex(epochSignatureInput(record))).toBe(epoch.record.signatureInput)
      expect(toHex(epochSignatureInput({ ...record, members: [...record.members].reverse() }))).toBe(epoch.record.signatureInput)
    })

    it('signs to the published bytes and verifies', async () => {
      const signature = await signEpoch(record, identityA)

      expect(toHex(signature)).toBe(epoch.record.signature)
      expect(await verifyEpoch(record, identityA.publicKey, signature)).toBe(true)
    })
  })
})

describe('messages', () => {
  it.each(vectors.messages.map((message) => [message.description, message] as const))('%s', async (_description, message) => {
    const context: MessageContext = message.context
    const epochKey = fromHex(vectors.epoch.epochKey)
    const expected = JSON.parse(message.payloadJson)

    expect(readMessageEpoch(fromHex(message.blob))).toBe(message.epoch)

    const opened = await openMessage({ context, epochKey, signerPublic: identityA.publicKey, blob: fromHex(message.blob) })
    expect(opened).toEqual(expected)

    const sealed = await sealMessage(
      { context, epoch: message.epoch, epochKey, payload: expected, signer: identityA },
      { nonce: fromHex(message.nonce) },
    )
    expect(toHex(sealed)).toBe(message.blob)
  })

  it('has the published signature input', () => {
    const message = vectors.messages[0]
    const blob = fromHex(message.blob)
    const context = Uint8Array.of(
      ...uuidBytes(message.context.conversationId),
      ...uuidBytes(message.context.senderId),
      ...uuidBytes(message.context.clientMessageId),
      0,
      0,
      0,
      message.context.editCount,
    )

    expect(toHex(messageSignatureInput(context, blob.subarray(0, 18), blob.subarray(18, blob.length - 64)))).toBe(message.signatureInput)
  })

  it('pads to a multiple of 256 bytes', () => {
    expect(vectors.messages.every((message) => message.paddedLength % 256 === 0)).toBe(true)
  })
})

describe('files', () => {
  it.each(vectors.files.map((file) => [file.description, file] as const))('%s', async (_description, file) => {
    const key = fromHex(file.key)
    const plain = fromHex(file.plain)
    const stored = fromHex(file.stored)

    expect(toHex(await encryptFile(key, plain, { chunkSize: file.chunkSize, noncePrefix: fromHex(file.noncePrefix) }))).toBe(file.stored)
    expect(toHex(await decryptFile(key, stored))).toBe(file.plain)
    expect(stored.length).toBe(encryptedFileLength(plain.length, file.chunkSize))
  })

  // A client that streams fetches one chunk by its byte range and decrypts it alone.
  it('opens a single chunk from its byte range', async () => {
    const file = vectors.files[0]
    const key = fromHex(file.key)
    const stored = fromHex(file.stored)
    const header = readFileHeader(stored)
    const start = chunkOffset(header, 1)

    const middle = await decryptChunk(key, header, 1, false, stored.subarray(start, start + header.chunkSize + 16))

    expect(toHex(middle)).toBe(file.plain.slice(64 * 2, 128 * 2))
  })
})

describe('device link', () => {
  const link = vectors.link
  const temporary = pairOf(link.temporary)

  it('derives the published code from the temporary public key', async () => {
    expect(await linkCode(temporary.publicKey)).toBe(link.code)
  })

  it('accepts the code as people type it', async () => {
    const lowered = link.code.toLowerCase()
    const withHyphen = formatLinkCode(link.code)

    expect(normalizeLinkCode(lowered)).toBe(link.code)
    expect(normalizeLinkCode(` ${withHyphen} `)).toBe(link.code)
    expect(await matchesLinkCode(withHyphen, temporary.publicKey)).toBe(true)
  })

  it('reads look-alike letters as Crockford does', () => {
    expect(normalizeLinkCode('OIL0O-IL0OI')).toBe('0110011001')
    expect(normalizeLinkCode('U')).toBeUndefined()
  })

  it('has the published address for a QR code', () => {
    expect(linkUri(link.requestId, temporary.publicKey)).toBe(link.uri)

    const parsed = parseLinkUri(link.uri)
    expect(parsed.requestId).toBe(link.requestId)
    expect(toHex(parsed.temporaryPublic)).toBe(link.temporary.public)
  })

  it('seals the secrets to the published bytes', async () => {
    const sealed = await sealLinkSecrets(link.requestId, link.userId, temporary.publicKey, secretsA, {
      ephemeralPrivate: fromHex(link.ephemeralPrivate),
      nonce: fromHex(link.nonce),
    })

    expect(toHex(sealed)).toBe(link.sealed)
  })

  it('opens the published box', async () => {
    const opened = await openLinkSecrets(link.requestId, link.userId, temporary, fromHex(link.sealed))

    expect(toHex(encodeSecrets(opened))).toBe(vectors.secrets.encoded)
  })
})

describe('safety number', () => {
  const { a, b, number } = vectors.safetyNumber

  it('matches the published number from either side', async () => {
    const first = { userId: a.userId, identityPublic: fromHex(a.identityPublic) }
    const second = { userId: b.userId, identityPublic: fromHex(b.identityPublic) }

    expect(await safetyNumber(first, second)).toBe(number)
    expect(await safetyNumber(second, first)).toBe(number)
    expect(number).toMatch(/^\d{60}$/)
  })

  it('is shown as twelve groups of five digits', () => {
    expect(formatSafetyNumber(number).split(' ')).toHaveLength(12)
  })

  it('belongs to these identity keys', () => {
    expect(a.identityPublic).toBe(toHex(identityA.publicKey))
    expect(b.identityPublic).toBe(toHex(identityB.publicKey))
  })
})
