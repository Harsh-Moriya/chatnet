import { vi } from 'vitest'
import type { FileRef } from '../../../lib/crypto/message'
import type { Attachment, AttachmentWire } from '../types'
import { refusalOf } from '../../encryption/epochApi'
import { withEpochRetries } from '../../encryption/epochs'

// For the tests of the chat API: the parts of the conversation keys that need a signed-in person, a server and this browser's stores,
// replaced by stand-ins that record how they were called. The rest of the module (the retries, the mapping of a refused epoch) is the
// real one. The keys themselves are tested against the real cryptography in features/encryption.

export const deps = { name: 'deps' } as never

export const prepared = {
  conversationId: 'made-id',
  body: { epoch: 1, commitment: 'commitment', signature: 'signature', envelopes: [{ userId: 'u1', generation: 1, envelope: 'envelope' }] },
  key: new Uint8Array(32).fill(7),
}

export const addition = {
  body: { epoch: 2, commitment: 'commitment-2', signature: 'signature-2', envelopes: [{ userId: 'u1', generation: 1, envelope: 'envelope-2' }] },
  key: new Uint8Array(32).fill(9),
}

// Sealing and opening, replaced by a tag in front of the text, so the tests of the chat do not need the cryptography: what is sealed here
// is what the stand-in for opening reads back, and anything else is a message that cannot be opened. The cryptography is tested against
// the real thing in features/encryption.
export const SEALED = 'sealed:'

// What separates the text from the entries of the files in what is sealed here.
const FILES_AT = '\u0001'

// The text and the entries of the files, sealed. A message with no files seals as the text alone.
export const sealed = (text: string, files: readonly FileRef[] = []) =>
  files.length === 0 ? `${SEALED}${text}` : `${SEALED}${text}${FILES_AT}${JSON.stringify(files)}`

// A message as the server sends it: its text and the entries of its files sealed, and the files as the server holds them, which say nothing
// of what they are.
export function wire<T extends { content: string | null; attachments?: Attachment[] }>(
  message: T,
): Omit<T, 'content' | 'attachments'> & { ciphertext: string | null; keyEpoch: number; attachments: AttachmentWire[] } {
  const { content, attachments = [], ...rest } = message
  const files = attachments.flatMap((attachment) => (attachment.file ? [attachment.file] : []))
  return {
    ...rest,
    ciphertext: content === null ? null : sealed(content, files),
    keyEpoch: 1,
    attachments: attachments.map(({ id, sizeBytes, url }) => ({ id, sizeBytes, url })),
  }
}

const isMessage = (value: unknown): value is { content: string | null } =>
  typeof value === 'object' && value !== null && 'content' in value && 'conversationId' in value

// What a server answers with, when the test wrote the answer as messages as the app holds them: a message or a list of them is sealed,
// anything else is left as it is.
export function asServerSends<T>(data: T): unknown {
  if (Array.isArray(data)) return data.map((item) => (isMessage(item) ? wire(item) : item))
  return isMessage(data) ? wire(data) : data
}

const openSealed = async (message: { ciphertext: string }) => {
  if (!message.ciphertext.startsWith(SEALED)) return { readable: false as const, reason: 'invalid' as const }

  const [text, files] = message.ciphertext.slice(SEALED.length).split(FILES_AT)
  return { readable: true as const, text, files: files === undefined ? [] : (JSON.parse(files) as FileRef[]) }
}

export const opener = {
  open: vi.fn(openSealed),
  forget: vi.fn(),
}

const sealAndSendThrough = async (
  _conversationId: string,
  _known: unknown,
  input: { text: string; files?: FileRef[] },
  send: (sealed: { ciphertext: string; keyEpoch: number }) => Promise<unknown>,
) => send({ ciphertext: sealed(input.text, input.files), keyEpoch: 1 })

export const keysDouble = {
  epochDeps: vi.fn(() => deps),
  messageOpener: vi.fn(() => opener),
  sealAndSend: vi.fn(sealAndSendThrough),
  prepareNewConversation: vi.fn(async () => prepared),
  keepNewConversationKey: vi.fn(async () => undefined),
  prepareAddingMembers: vi.fn(async () => addition),
  saveKey: vi.fn(async () => undefined),
}

// What the chat gets in place of the conversation keys module: the stand-ins, and the retry loop and the mapping of a refusal as they are.
// Written out instead of taken from the module, so the stand-ins do not wait for the real module (and the keys, stores and workers it
// reaches) to load.
export const keysModule = { ...keysDouble, withEpochRetries, refusalOf }

// Clears what was recorded and puts every stand-in back to how it behaves, which a test that restores all mocks after itself
// (vi.restoreAllMocks) would otherwise leave emptied for the tests that follow.
export function resetKeysDouble(): void {
  keysDouble.epochDeps.mockReset().mockImplementation(() => deps)
  keysDouble.messageOpener.mockReset().mockImplementation(() => opener)
  keysDouble.sealAndSend.mockReset().mockImplementation(sealAndSendThrough)
  keysDouble.prepareNewConversation.mockReset().mockImplementation(async () => prepared)
  keysDouble.keepNewConversationKey.mockReset().mockImplementation(async () => undefined)
  keysDouble.prepareAddingMembers.mockReset().mockImplementation(async () => addition)
  keysDouble.saveKey.mockReset().mockImplementation(async () => undefined)
  opener.open.mockReset().mockImplementation(openSealed)
  opener.forget.mockReset()
}
