import { useAuthStore } from '../auth/store/authStore'
import { epochApi } from './epochApi'
import { indexedDbEpochKeyStore } from './epochKeyStore'
import { type EpochDeps, type KnownKeyState, MissingKeysError, readyConversationWith } from './epochs'
import { useKeyStatusStore } from './keyStatus'
import { indexedDbPinStore } from './pinStore'
import { type MessageOpener, type Sealed, createMessageOpener, sealText, underCurrentEpoch } from './sealedMessages'
import { useEncryptionStore } from './store'

// The conversation keys joined to the real server, this browser's stores and the signed-in person. The rules are in epochs.ts. The chat
// loads this module on first use (it carries the cryptography), so a page that never starts a conversation or opens one that needs
// keys does not load it.

export * from './epochs'
export { refusalOf } from './epochApi'
export type { OpenedMessage, SealedMessage } from './sealedMessages'

// The keys of the signed-in person are not in this browser: nobody is signed in, or the gate has not let them through.
export class EncryptionNotReadyError extends Error {
  constructor() {
    super('The encryption keys are not available.')
    this.name = 'EncryptionNotReadyError'
  }
}

export function epochDeps(): EpochDeps {
  const userId = useAuthStore.getState().user?.userId
  const secrets = useEncryptionStore.getState().secrets
  if (userId === undefined || secrets === null) throw new EncryptionNotReadyError()

  return { api: epochApi, keys: indexedDbEpochKeyStore, pins: indexedDbPinStore, self: { userId, secrets } }
}

// Conversations being made ready right now, so opening one twice (or two tabs of the same page in one browser reacting to the same push)
// does not make its keys twice at once.
const inFlight = new Set<string>()

// Makes the conversation ready to use: its newest epoch exists, was made for the people in it and is held here. Failing is not an error
// the person is told about: the conversation works as it did, and this is tried again the next time it is opened. One failure is shown,
// though, because it is not going to mend by itself: someone in the conversation has not set up encryption, so no key can be sealed for
// them, and the box where a message is written says who it is waiting for. Answers whether the conversation is ready.
export async function readyConversation(conversationId: string, known: KnownKeyState): Promise<boolean> {
  if (inFlight.has(conversationId)) return false
  inFlight.add(conversationId)

  try {
    await readyConversationWith(epochDeps(), conversationId, known)
    useKeyStatusStore.getState().clear(conversationId)
    return true
  } catch (failure) {
    if (failure instanceof MissingKeysError) useKeyStatusStore.getState().setWaitingFor(conversationId, failure.userIds)
    return false
  } finally {
    inFlight.delete(conversationId)
  }
}

// ##### Messages #####

// The opener for the signed-in person, which keeps what it learned of keys and senders for as long as the same person is signed in.
let opener: { userId: string; opener: MessageOpener } | null = null

export function messageOpener(): MessageOpener {
  const deps = epochDeps()
  if (opener?.userId !== deps.self.userId) opener = { userId: deps.self.userId, opener: createMessageOpener(deps) }

  return opener.opener
}

export interface SendInput {
  clientMessageId: string
  // 0 for the first version of a message, and one more for each edit.
  editCount: number
  text: string
}

// Seals the text under the epoch the conversation is in and hands it to `send`, which makes the request. When the server says the epoch
// was the wrong one, the keys are looked at again and the text is sealed anew, a few times (see underCurrentEpoch).
export async function sealAndSend<T>(
  conversationId: string,
  known: KnownKeyState,
  input: SendInput,
  send: (sealed: Sealed) => Promise<T>,
): Promise<T> {
  const deps = epochDeps()
  return underCurrentEpoch(deps, conversationId, known, async (epoch) => send(await sealText(deps, { conversationId, epoch, ...input })))
}
