import { useAuthStore } from '../auth/store/authStore'
import { epochApi } from './epochApi'
import { indexedDbEpochKeyStore } from './epochKeyStore'
import { type EpochDeps, type KnownKeyState, readyConversationWith } from './epochs'
import { indexedDbPinStore } from './pinStore'
import { useEncryptionStore } from './store'

// The conversation keys joined to the real server, this browser's stores and the signed-in person. The rules are in epochs.ts. The chat
// loads this module on first use (it carries the cryptography), so a page that never starts a conversation or opens one that needs
// keys does not load it.

export * from './epochs'
export { refusalOf } from './epochApi'

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
// the person is told about: the conversation works as it did, and this is tried again the next time it is opened.
export async function readyConversation(conversationId: string, known: KnownKeyState): Promise<void> {
  if (inFlight.has(conversationId)) return
  inFlight.add(conversationId)

  try {
    await readyConversationWith(epochDeps(), conversationId, known)
  } catch {
    // Left as it was.
  } finally {
    inFlight.delete(conversationId)
  }
}
